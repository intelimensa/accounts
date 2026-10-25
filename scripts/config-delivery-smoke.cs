#:project ../Intelimensa.Accounts/Intelimensa.Accounts.csproj
#:property PublishAot=false
// End-to-end check of config delivery against a running test server (scripts/test-server.sh start), playing AxoSync:
// registers an install at login, signs every request, has staff upload a config, then fetches, unwraps and decrypts it,
// and walks through the ways it must be refused. Uses the server's own crypto classes for the client side; the
// independent check of the formats is testdata/config-delivery-vectors.json.
//
//   scripts/test-server.sh start && dotnet run scripts/config-delivery-smoke.cs; scripts/test-server.sh stop
//
// It seeds the test server's throwaway database directly (sqlite3) for what has no API yet: activating the test account
// and pairing it with a unit. Never point it at a real database: it refuses any data directory but the test server's.
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Intelimensa.Accounts.ConfigDelivery;
using Intelimensa.Accounts.Security;

var dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp", "intelimensa-accounts-test");
if (!File.Exists(Path.Combine(dir, ".test-server-marker")))
    return Fail($"no test server data directory at {dir}: run scripts/test-server.sh start first");
var baseUrl = File.ReadAllText(Path.Combine(dir, "url")).Trim();
var env = File.ReadAllLines(Path.Combine(dir, "delivery.env")).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
var devSecret = Convert.FromBase64String(env["DEVELOPMENT_RELEASE_SECRET"]);
var leasePublicKey = env["LEASE_PUBLIC_KEY"];
var db = Path.Combine(dir, "accounts.db");
const string Email = "test@example.com", Password = "correct-horse-battery", Serial = "MSV3G01SATKX";
var failures = 0;

void Check(bool ok, string what)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
    if (!ok) failures++;
}

string Sql(string sql)
{
    var psi = new ProcessStartInfo("sqlite3", [db, sql]) { RedirectStandardOutput = true, RedirectStandardError = true };
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) throw new InvalidOperationException(p.StandardError.ReadToEnd());
    return output.Trim();
}

// ---- seed: an active account, a unit paired with it ------------------------------------------------------------------
Console.WriteLine("Seeding the test account and a unit");
Sql($"UPDATE AspNetUsers SET EmailConfirmed = 1 WHERE Email = '{Email}';" +
    $"UPDATE Accounts SET Status = 0 WHERE UserId = (SELECT Id FROM AspNetUsers WHERE Email = '{Email}');");
var accountId = int.Parse(Sql($"SELECT a.Id FROM Accounts a JOIN AspNetUsers u ON u.Id = a.UserId WHERE u.Email = '{Email}';"));
var unitId = Guid.NewGuid().ToString().ToUpperInvariant();
Sql($"INSERT INTO BciDevices (Id, CreatedAt, DeviceType, ProducedAt, SerialNumber, Status) VALUES ('{unitId}', '2026-10-25 00:00:00+00:00', 'msv3', '2026-10-25', '{Serial}', 1);" +
    $"INSERT INTO AccountDevices (Id, AccountId, BciDeviceId, RegisteredAt) VALUES ('{Guid.NewGuid().ToString().ToUpperInvariant()}', {accountId}, '{unitId}', '2026-10-25 00:00:00+00:00');");

// ---- staff: create a config and upload its file (the web page, as a person would) -------------------------------------
Console.WriteLine("Staff upload a config");
var plaintext = "[CONEXUS_BCI_CONFIGURATION]\nSECTIONS = INPUT\n\n[INPUT]\nPOLLING_PERIOD = 0.25\n";
var cek = RandomNumberGenerator.GetBytes(32);
var blob = ConfigBlob.Encrypt(Encoding.UTF8.GetBytes(plaintext), cek, RandomNumberGenerator.GetBytes(12));
var blobSha = Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant();

var web = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri(baseUrl) };
async Task<string> Antiforgery(string path) =>
    Regex.Match(await web.GetStringAsync(path), "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]*)\"").Groups[1].Value;

await web.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["__RequestVerificationToken"] = await Antiforgery("/Account/Login"), ["Input.Email"] = Email, ["Input.Password"] = Password,
}));
await web.PostAsync("/Staff/Configs?handler=Create", new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["__RequestVerificationToken"] = await Antiforgery("/Staff/Configs"), ["Input.Key"] = "ms5", ["Input.DisplayName"] = "MS5 (smoke test)",
}));
var configId = int.Parse(Sql("SELECT Id FROM Configs WHERE Key = 'ms5';"));

async Task<string> Upload(byte[] file, string key)
{
    var form = new MultipartFormDataContent
    {
        { new StringContent(await Antiforgery("/Staff/Configs")), "__RequestVerificationToken" },
        { new StringContent(configId.ToString()), "configId" },
        { new ByteArrayContent(file), "blob", "ms5.cfg" },
        { new ByteArrayContent(Encoding.ASCII.GetBytes(key + "\n")), "key", "ms5.key" },
    };
    return await (await web.PostAsync("/Staff/Configs?handler=Upload", form)).Content.ReadAsStringAsync();
}
Check((await Upload(blob, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))).Contains("doesn&#x27;t open this config"),
    "an upload whose key doesn't open the file is refused");
await Upload(blob, Convert.ToBase64String(cek));
Check(Sql("SELECT BlobSha256 FROM Configs WHERE Key = 'ms5';") == blobSha, "the matching pair is stored");
Check(!Sql("SELECT WrappedCek FROM Configs WHERE Key = 'ms5';").Contains(Convert.ToBase64String(cek)), "the key is stored wrapped, not as uploaded");

// ---- AxoSync: an install signs in --------------------------------------------------------------------------------------
var api = new HttpClient { BaseAddress = new Uri(baseUrl) };
string nonce = "";

async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body, string? bearer, ECDsa? signer,
    string? overridePath = null)
{
    var json = body is null ? "" : JsonSerializer.Serialize(body, JsonSerializerOptions.Web);
    var request = new HttpRequestMessage(method, path);
    if (body is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
    if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
    if (signer is not null)
    {
        var statement = InstallProof.Statement(method.Method, overridePath ?? path, InstallProof.BodySha256(Encoding.UTF8.GetBytes(json)), nonce);
        request.Headers.Add(InstallProof.NonceHeader, nonce);
        request.Headers.Add(InstallProof.ProofHeader,
            Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes(statement), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
    }
    var response = await api.SendAsync(request);
    if (response.Headers.TryGetValues(InstallProof.NonceHeader, out var next)) nonce = next.First();
    return response;
}

async Task<string?> Error(HttpResponseMessage r)
{
    try { return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString(); }
    catch { return null; }
}

Console.WriteLine("AxoSync signs in as a new install");
using var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
using var agreement = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
var install = new
{
    signingKey = Convert.ToBase64String(signing.ExportSubjectPublicKeyInfo()),
    agreementKey = Convert.ToBase64String(agreement.ExportSubjectPublicKeyInfo()),
    platform = "MacOS", name = "Smoke test", appVersion = "development", slot = 1,
};
var login = new { email = Email, password = Password, install };

await Send(HttpMethod.Get, "/api/auth/nonce", null, null, null);
Check(nonce.Length > 0, "every API response carries a nonce");

var r = await Send(HttpMethod.Post, "/api/auth/login", login, null, null);
Check(r.StatusCode == HttpStatusCode.Unauthorized && await Error(r) == "install_proof_required", "registering an install without its proof is refused");
using (var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256))
{
    r = await Send(HttpMethod.Post, "/api/auth/login", login, null, stranger);
    Check(r.StatusCode == HttpStatusCode.Unauthorized && await Error(r) == "install_proof_invalid", "a proof by another key is refused");
}

r = await Send(HttpMethod.Post, "/api/auth/login", login, null, signing);
Check(r.IsSuccessStatusCode, $"login with the install's proof ({(int)r.StatusCode})");
var tokens = await r.Content.ReadFromJsonAsync<JsonElement>();
var access = tokens.GetProperty("accessToken").GetString()!;
var refresh = tokens.GetProperty("refreshToken").GetString()!;
Check(Sql($"SELECT COUNT(*) FROM ClientInstalls WHERE AccountId = {accountId};") == "1", "one install row");

r = await Send(HttpMethod.Post, "/api/auth/login", login, null, signing);
Check(r.IsSuccessStatusCode && Sql($"SELECT COUNT(*) FROM ClientInstalls WHERE AccountId = {accountId};") == "1",
    "signing in again with the same key reuses the install");
access = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;

// ---- delivery ----------------------------------------------------------------------------------------------------------
const string ConfigPath = $"/api/devices/{Serial}/config?version=development&platform=MacOSArm64";
Console.WriteLine("Delivery before the unit has a config");
r = await Send(HttpMethod.Get, ConfigPath, null, access, signing);
Check(r.StatusCode == HttpStatusCode.NotFound && await Error(r) == "not_assigned", "no assignment: not_assigned");

Sql($"UPDATE AccountDevices SET AssignedConfigId = {configId} WHERE BciDeviceId = '{unitId}';");
Console.WriteLine("Delivery");
r = await Send(HttpMethod.Get, ConfigPath, null, access, null);
Check(r.StatusCode == HttpStatusCode.Unauthorized && await Error(r) == "install_proof_required", "a bound token without a proof gets nothing");
r = await Send(HttpMethod.Get, ConfigPath, null, access, signing, overridePath: ConfigPath.Replace("development", "1.0.0"));
Check(r.StatusCode == HttpStatusCode.Unauthorized && await Error(r) == "install_proof_invalid", "a proof for another URL is refused");
r = await Send(HttpMethod.Get, $"/api/devices/{Serial}/config?version=9.9.9&platform=MacOSArm64", null, access, signing);
Check(r.StatusCode == HttpStatusCode.Forbidden && await Error(r) == "build_not_supported", "an unknown build: build_not_supported");
r = await Send(HttpMethod.Get, "/api/devices/MSV3G01SATKY/config?version=development&platform=MacOSArm64", null, access, signing);
Check(r.StatusCode == HttpStatusCode.NotFound && await Error(r) == "not_registered", "someone else's or an unknown unit: not_registered");

r = await Send(HttpMethod.Get, ConfigPath, null, access, signing);
Check(r.IsSuccessStatusCode, $"the config is delivered ({(int)r.StatusCode})");
Check(r.Headers.CacheControl?.NoStore == true, "with no-store");
var delivery = await r.Content.ReadFromJsonAsync<JsonElement>();
var lease = delivery.GetProperty("lease");
var w = delivery.GetProperty("wrappedCek");
var installId = lease.GetProperty("clientInstallId").GetGuid();
var wrapped = new WrappedCek(Convert.FromBase64String(w.GetProperty("ephemeralKey").GetString()!), Convert.FromBase64String(w.GetProperty("nonce").GetString()!),
    Convert.FromBase64String(w.GetProperty("ciphertext").GetString()!), Convert.FromBase64String(w.GetProperty("tag").GetString()!));
var unwrapped = CekWrap.Unwrap(agreement, wrapped, devSecret, installId, "ms5", blobSha);
Check(unwrapped.SequenceEqual(cek), "the install unwraps the config's key with its agreement key and the build's secret");
Check(Throws(() => CekWrap.Unwrap(agreement, wrapped, RandomNumberGenerator.GetBytes(32), installId, "ms5", blobSha)),
    "another build's secret doesn't open it");

var terms = new LeaseTerms(lease.GetProperty("accountId").GetInt32(), installId, lease.GetProperty("serialNumber").GetString()!,
    lease.GetProperty("configKey").GetString()!, lease.GetProperty("blobSha256").GetString()!,
    DateTimeOffset.Parse(lease.GetProperty("issuedAt").GetString()!), DateTimeOffset.Parse(lease.GetProperty("notAfter").GetString()!));
Check(OfflineLease.Verify(leasePublicKey, OfflineLease.Statement(terms), delivery.GetProperty("leaseSignature").GetString()!),
    $"the lease verifies against the lease public key (until {lease.GetProperty("notAfter").GetString()})");
Check((terms.NotAfter - terms.IssuedAt).TotalDays == 7, "a 7-day lease by default");

r = await Send(HttpMethod.Get, delivery.GetProperty("blobUrl").GetString()!, null, access, signing);
var downloaded = await r.Content.ReadAsByteArrayAsync();
Check(r.IsSuccessStatusCode && r.Headers.ETag?.Tag == $"\"{blobSha}\"", "the blob downloads with its SHA-256 as ETag");
Check(Encoding.UTF8.GetString(ConfigBlob.Decrypt(downloaded, unwrapped)) == plaintext, "and decrypts to the config");
Check(Sql($"SELECT COUNT(*) FROM ConfigKeyIssuances WHERE ClientInstallId = '{installId.ToString().ToUpperInvariant()}';") == "1",
    "the issuance is recorded");

// ---- refresh -----------------------------------------------------------------------------------------------------------
Console.WriteLine("Refresh");
r = await Send(HttpMethod.Post, "/api/auth/refresh", new { refreshToken = refresh }, null, null);
Check(r.StatusCode == HttpStatusCode.Unauthorized, "a bound refresh token without the install's proof is refused");
r = await Send(HttpMethod.Post, "/api/auth/refresh", new { refreshToken = refresh }, null, signing);
Check(r.IsSuccessStatusCode, "with the proof it is renewed");
access = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
r = await Send(HttpMethod.Get, ConfigPath, null, access, signing);
Check(r.IsSuccessStatusCode, "and the renewed token is still bound to the install");

// ---- a login without an install (Quarry, publish scripts, older AxoSync) ---------------------------------------------
Console.WriteLine("Unbound tokens");
r = await Send(HttpMethod.Post, "/api/auth/login", new { email = Email, password = Password }, null, null);
var unbound = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
r = await Send(HttpMethod.Get, "/api/releases/latest?platform=MacOSArm64&current=0.1.0", null, unbound, null);
Check(r.IsSuccessStatusCode, "still work for the update check");
r = await Send(HttpMethod.Get, ConfigPath, null, unbound, null);
Check(r.StatusCode == HttpStatusCode.Forbidden, "but get no config");

// ---- a published build with its release secret, then blocked -----------------------------------------------------------
Console.WriteLine("A published build");
var secret = RandomNumberGenerator.GetBytes(32);
await Send(HttpMethod.Put, "/api/publishing/releases/1.0.0", new { notes = "smoke" }, unbound, null);
var upload = new HttpRequestMessage(HttpMethod.Put, "/api/publishing/releases/1.0.0/artifacts/MacOSArm64?fileName=AxoSync-1.0.0.dmg")
{
    Content = new ByteArrayContent(RandomNumberGenerator.GetBytes(1024)),
};
upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", unbound);
upload.Headers.Add("X-Release-Secret", Convert.ToBase64String(secret));
r = await api.SendAsync(upload);
Check(r.IsSuccessStatusCode && (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasReleaseSecret").GetBoolean(),
    "the build is uploaded with its release secret");
const string ReleasePath = $"/api/devices/{Serial}/config?version=1.0.0&platform=MacOSArm64";
r = await Send(HttpMethod.Get, ReleasePath, null, access, signing);
Check(r.StatusCode == HttpStatusCode.Forbidden, "a draft build gets nothing");
await Send(HttpMethod.Post, "/api/publishing/releases/1.0.0/publish", null, unbound, null);
r = await Send(HttpMethod.Get, ReleasePath, null, access, signing);
var forRelease = await r.Content.ReadFromJsonAsync<JsonElement>();
var w2 = forRelease.GetProperty("wrappedCek");
var wrapped2 = new WrappedCek(Convert.FromBase64String(w2.GetProperty("ephemeralKey").GetString()!), Convert.FromBase64String(w2.GetProperty("nonce").GetString()!),
    Convert.FromBase64String(w2.GetProperty("ciphertext").GetString()!), Convert.FromBase64String(w2.GetProperty("tag").GetString()!));
Check(CekWrap.Unwrap(agreement, wrapped2, secret, installId, "ms5", blobSha).SequenceEqual(cek), "the published build opens its key with its own secret");
Check(Throws(() => CekWrap.Unwrap(agreement, wrapped2, devSecret, installId, "ms5", blobSha)), "and only with its own");
Sql("UPDATE ReleaseArtifacts SET ConfigDeliveryBlockedAt = '2026-10-25 00:00:00+00:00';");
r = await Send(HttpMethod.Get, ReleasePath, null, access, signing);
Check(r.StatusCode == HttpStatusCode.Forbidden && await Error(r) == "build_not_supported", "a blocked build gets build_not_supported");

// ---- limits and removal ----------------------------------------------------------------------------------------------
Console.WriteLine("Install limit and removal");
Sql($"UPDATE Accounts SET MaxInstallsOverride = 1 WHERE Id = {accountId};");
using (var second = ECDsa.Create(ECCurve.NamedCurves.nistP256))
using (var secondAgreement = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
{
    var other = new
    {
        email = Email, password = Password,
        install = new
        {
            signingKey = Convert.ToBase64String(second.ExportSubjectPublicKeyInfo()),
            agreementKey = Convert.ToBase64String(secondAgreement.ExportSubjectPublicKeyInfo()), platform = "Windows",
        },
    };
    r = await Send(HttpMethod.Post, "/api/auth/login", other, null, second);
    Check(r.StatusCode == HttpStatusCode.Conflict && await Error(r) == "install_limit", "a second install over a limit of 1: install_limit");
}

Sql($"UPDATE ClientInstalls SET RevokedAt = '2026-10-25 00:00:00+00:00' WHERE Id = '{installId.ToString().ToUpperInvariant()}';");
r = await Send(HttpMethod.Get, ConfigPath, null, access, signing);
Check(r.StatusCode == HttpStatusCode.Unauthorized && await Error(r) == "install_revoked", "a removed install's token stops working at once");
r = await Send(HttpMethod.Post, "/api/auth/login", login, null, signing);
Check(r.StatusCode == HttpStatusCode.BadRequest, "and a removed install's key can't sign in again");

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) FAILED.");
return failures == 0 ? 0 : 1;

static bool Throws(Action action)
{
    try { action(); return false; }
    catch (CryptographicException) { return true; }
}

static int Fail(string message)
{
    Console.Error.WriteLine($"config-delivery-smoke: {message}");
    return 2;
}
