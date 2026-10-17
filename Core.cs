using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace WorkBuddyDaily {
    public static class WorkBuddyExit {
        static Dictionary<int, DateTime> Snapshot(string application) {
            var targets = new Dictionary<int, DateTime>();
            foreach (Process process in Process.GetProcessesByName("WorkBuddy")) using (process) {
                try { if (String.Equals(process.MainModule.FileName, application, StringComparison.OrdinalIgnoreCase)) targets[process.Id] = process.StartTime; } catch { }
            }
            return targets;
        }
        static Process Match(int id, DateTime started, string application) {
            Process process = null;
            try {
                process = Process.GetProcessById(id);
                if (process.StartTime == started && String.Equals(process.MainModule.FileName, application, StringComparison.OrdinalIgnoreCase)) return process;
            } catch { }
            if (process != null) process.Dispose();
            return null;
        }
        public static async Task<string> AfterSuccess(string application, CancellationToken ct) {
            application = Path.GetFullPath(application);
            if (!String.Equals(Path.GetFileName(application), "WorkBuddy.exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择 WorkBuddy.exe。");
            var targets = Snapshot(application);
            await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            bool pending = false;
            foreach (var target in targets) using (Process process = Match(target.Key, target.Value, application)) {
                if (process == null) continue;
                pending = true;
                try { if (process.MainWindowHandle != IntPtr.Zero) process.CloseMainWindow(); } catch { }
            }
            if (pending) await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            return await Task.Run(delegate {
                bool failed = false;
                foreach (var target in targets) {
                    ct.ThrowIfCancellationRequested();
                    using (Process process = Match(target.Key, target.Value, application)) {
                        if (process == null) continue;
                        try { process.Kill(); if (!process.WaitForExit(3000)) failed = true; }
                        catch { if (!process.HasExited) failed = true; }
                    }
                }
                foreach (var target in targets) using (Process process = Match(target.Key, target.Value, application)) { if (process != null) failed = true; }
                return failed ? "签到已完成；WorkBuddy 仍在运行，请手动检查" : "签到已完成，WorkBuddy 已退出";
            }, ct).ConfigureAwait(false);
        }
    }
    public sealed class Preferences {
        public string Time = "09:00", ApplicationPath = "", LastAccount = "", LastDay = "";
        public bool Enabled = false, Startup = false, CloseAfterCheckin = false;
    }
    public sealed class Account {
        public string AccessToken = "", RefreshToken = "", Uid = "", Enterprise = "", Nickname = "", Domain = "codebuddy.cn";
        public long ExpiresAt;
        public string Identity { get { using (SHA256 hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(Uid))); } }
    }
    public sealed class Store {
        public readonly string DirectoryPath;
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1048576 };
        public Store(string directory) { DirectoryPath = directory; Directory.CreateDirectory(directory); }
        public static void WriteAtomic(string file, byte[] data) {
            string temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
            try { File.WriteAllBytes(temp, data); if (File.Exists(file)) File.Replace(temp, file, file + ".bak"); else File.Move(temp, file); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public Preferences Load() {
            string file = Path.Combine(DirectoryPath, "settings.json");
            return File.Exists(file) ? Json.Deserialize<Preferences>(File.ReadAllText(file)) : new Preferences();
        }
        public void Save(Preferences p) { WriteAtomic(Path.Combine(DirectoryPath, "settings.json"), Encoding.UTF8.GetBytes(Json.Serialize(p))); }
        public void SaveAccount(Account account) {
            byte[] plain = Encoding.UTF8.GetBytes(Json.Serialize(account));
            try { WriteAtomic(Path.Combine(DirectoryPath, "account.dat"), ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        public Account LoadAccount() {
            string file = Path.Combine(DirectoryPath, "account.dat");
            if (!File.Exists(file)) return null;
            byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
            try { return Json.Deserialize<Account>(Encoding.UTF8.GetString(bytes)); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
        public void Log(string message) {
            string file = Path.Combine(DirectoryPath, "activity.log");
            if (File.Exists(file) && new FileInfo(file).Length > 150000) File.WriteAllText(file, "", Encoding.UTF8);
            File.AppendAllText(file, DateTime.Now.ToString("MM-dd HH:mm:ss") + "  " + message + Environment.NewLine, Encoding.UTF8);
        }
    }
    public static class Schedule {
        public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        public static DateTime Now(DateTime utc) { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone); }
        public static string Day(DateTime utc) { return Now(utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        public static TimeSpan Parse(string text) {
            TimeSpan time;
            if (!TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out time) || time.TotalHours >= 24) throw new Exception("时间请填写为 00:00 至 23:59。");
            return time;
        }
        public static bool Done(Preferences p, Account a, DateTime utc) { return a != null && p.LastAccount == a.Identity && p.LastDay == Day(utc); }
        public static DateTime DueUtc(Preferences p, Account a, DateTime utc) {
            DateTime local = Now(utc), due = local.Date.Add(Parse(p.Time));
            if (Done(p, a, utc)) due = due.AddDays(1);
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(due, DateTimeKind.Unspecified), Zone);
        }
    }
    public sealed class Reply {
        public int Code, HttpStatus;
        public Dictionary<string, object> Data = new Dictionary<string, object>();
    }
    public sealed class ServiceException : Exception {
        public readonly int Code, HttpStatus;
        public ServiceException(int code, int http) : base("服务响应未确认成功") { Code = code; HttpStatus = http; }
        public bool Auth { get { return HttpStatus == 401 || HttpStatus == 403 || Code == 12153; } }
        public bool Pending { get { return (Code == 11217 || Code == 1001) && (HttpStatus == 200 || HttpStatus == 400 || HttpStatus == 409); } }
        public bool Already { get { return Code == 10001 || Code == 14001; } }
    }
    public sealed class Api : IDisposable {
        public const string AuthBase = "https://copilot.tencent.com", BillingBase = "https://www.codebuddy.cn";
        readonly HttpClient http;
        public Func<string, string, Dictionary<string, string>, CancellationToken, Task<Reply>> Fake;
        public Api() {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer(), UseCookies = true };
            if (WebRequest.DefaultWebProxy != null) { handler.Proxy = WebRequest.DefaultWebProxy; handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials; }
            http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1048576 };
        }
        public static string Text(Dictionary<string, object> data, string key) { object value; return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
        public static long Number(Dictionary<string, object> data, string key) { long value; return Int64.TryParse(Text(data, key), out value) ? value : 0; }
        public static bool TrustedLogin(string value) {
            Uri u; if (!Uri.TryCreate(value, UriKind.Absolute, out u) || u.Scheme != "https" || u.Port != 443 || u.UserInfo != "") return false;
            string host = u.DnsSafeHost.ToLowerInvariant();
            return host == "codebuddy.cn" || host.EndsWith(".codebuddy.cn", StringComparison.Ordinal) || host == "copilot.tencent.com";
        }
        public async Task<Dictionary<string, object>> Call(string url, string method, Dictionary<string, string> headers, CancellationToken ct) {
            Uri target = new Uri(url);
            if (target.Scheme != "https" || target.Port != 443 || target.UserInfo != "" || (target.Host != "copilot.tencent.com" && target.Host != "www.codebuddy.cn")) throw new Exception("请求地址不受支持。");
            Reply reply;
            if (Fake != null) reply = await Fake(url, method, headers, ct);
            else {
                using (var message = new HttpRequestMessage(new HttpMethod(method), url)) {
                    message.Headers.TryAddWithoutValidation("User-Agent", "CLI/2.63.2 CodeBuddy/2.63.2");
                    message.Headers.TryAddWithoutValidation("Origin", BillingBase);
                    message.Headers.TryAddWithoutValidation("Referer", BillingBase + "/");
                    message.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                    message.Headers.TryAddWithoutValidation("Accept", "application/json");
                    if (headers != null) foreach (var pair in headers) message.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                    if (method == "POST") message.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using (var response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct)) {
                        int status = (int)response.StatusCode;
                        if (status >= 300 && status < 400) throw new ServiceException(-1, status);
                        reply = ParseReply(await response.Content.ReadAsStringAsync(), status);
                    }
                }
            }
            if (reply.Code != 0 || reply.HttpStatus != 200) throw new ServiceException(reply.Code, reply.HttpStatus);
            return reply.Data;
        }
        public static Reply ParseReply(string body, int status) {
            var envelope = Store.Json.Deserialize<Dictionary<string, object>>(body);
            if (envelope == null || !envelope.ContainsKey("code")) throw new Exception("服务返回格式已变化。");
            int code;
            if (!Int32.TryParse(Text(envelope, "code"), out code)) throw new Exception("服务返回格式已变化。");
            object data;
            return new Reply { Code = code, HttpStatus = status, Data = envelope.TryGetValue("data", out data) && data is Dictionary<string, object> ? (Dictionary<string, object>)data : new Dictionary<string, object>() };
        }
        public static Dictionary<string, string> Headers(Account account) {
            return new Dictionary<string, string> { { "Authorization", "Bearer " + account.AccessToken }, { "X-User-Id", account.Uid }, { "X-Domain", account.Domain }, { "X-Enterprise-Id", account.Enterprise }, { "X-Tenant-Id", account.Enterprise } };
        }
        static void Tokens(Account account, Dictionary<string, object> data) {
            string access = Text(data, "accessToken"), refresh = Text(data, "refreshToken"), domain = Text(data, "domain");
            if (access.Length < 10 || access.Length > 16000) throw new Exception("未取得有效登录状态。");
            if (domain != "" && domain != "codebuddy.cn" && domain != "www.codebuddy.cn" && domain != "copilot.tencent.com") throw new Exception("目前仅支持国内版每日签到。");
            account.AccessToken = access;
            if (refresh != "") account.RefreshToken = refresh;
            account.Domain = domain == "" ? "codebuddy.cn" : domain;
            long seconds = Number(data, "expiresIn");
            account.ExpiresAt = seconds > 0 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Min(seconds, 31536000) : 0;
        }
        public async Task<Account> FinishLogin(string state, CancellationToken ct) {
            string query = "?state=" + Uri.EscapeDataString(state);
            var token = await Call(AuthBase + "/v2/plugin/auth/token" + query, "GET", null, ct);
            var account = new Account(); Tokens(account, token);
            var profile = await Call(AuthBase + "/v2/plugin/login/account" + query, "GET", Headers(account), ct);
            account.Uid = Text(profile, "uid"); account.Nickname = Text(profile, "nickname"); account.Enterprise = Text(profile, "enterpriseId");
            if (account.Uid == "") throw new Exception("登录成功，但账号资料不完整。");
            return account;
        }
        public async Task Refresh(Account account, Store store, CancellationToken ct) {
            if (account.RefreshToken == "") throw new ServiceException(12153, 401);
            var headers = new Dictionary<string, string> { { "X-Refresh-Token", account.RefreshToken }, { "X-Auth-Refresh-Source", "workbuddy" }, { "X-Enterprise-Id", account.Enterprise } };
            var response = await Call(AuthBase + "/v2/plugin/auth/token/refresh", "POST", headers, ct);
            Tokens(account, response); store.SaveAccount(account);
        }
        public async Task<string> Checkin(Account account, Store store, CancellationToken ct) {
            if (account.ExpiresAt > 0 && account.ExpiresAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600) await Refresh(account, store, ct);
            for (int attempt = 0; attempt < 2; attempt++) {
                try {
                    await Call(BillingBase + "/v2/billing/meter/daily-checkin", "POST", Headers(account), ct);
                    return "签到完成，服务端已确认";
                } catch (ServiceException ex) {
                    if (ex.Already) return "今日已签到，无需重复领取";
                    if (!ex.Auth || attempt != 0) throw;
                }
                await Refresh(account, store, ct);
            }
            throw new Exception("未确认签到结果。");
        }
        public void Dispose() { http.Dispose(); }
    }
}
