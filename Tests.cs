using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace WorkBuddyDaily {
    public static class Tests {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static Dictionary<string, object> Data(params object[] values) { var data = new Dictionary<string, object>(); for (int i = 0; i < values.Length; i += 2) data[(string)values[i]] = values[i + 1]; return data; }
        public static void Run(string output) {
            string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)), "isolated-" + Guid.NewGuid().ToString("N"));
            try {
                var store = new Store(temp);
                var p = store.Load(); Check(!p.Enabled && !p.Startup, "Clean defaults");
                var a = new Account { AccessToken = "synthetic-access-token", RefreshToken = "synthetic-refresh-token", Uid = "synthetic-user" };
                store.SaveAccount(a); Check(store.LoadAccount().AccessToken == a.AccessToken, "Encrypted account round trip");
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(temp, "account.dat"))).Contains(a.AccessToken), "No plaintext token");
                DateTime before = new DateTime(2026, 10, 16, 0, 59, 0, DateTimeKind.Utc), after = before.AddMinutes(2);
                Check(Schedule.DueUtc(p, a, before) == before.AddMinutes(1), "Before deadline");
                Check(Schedule.DueUtc(p, a, after) < after, "Late start catches up");
                p.LastDay = Schedule.Day(after); p.LastAccount = a.Identity;
                Check(Schedule.Done(p, a, after) && Schedule.DueUtc(p, a, after) > after, "Daily deduplication");
                Check(!Schedule.Done(p, new Account { Uid = "other-user" }, after), "Account-specific deduplication");
                Check(Schedule.Day(new DateTime(2026, 10, 16, 16, 0, 0, DateTimeKind.Utc)) == "2026-10-17", "Beijing midnight");
                foreach (string bad in new[] { "24:00", "09:70", "text", "9:0" }) { bool failed = false; try { Schedule.Parse(bad); } catch { failed = true; } Check(failed, "Reject invalid schedule"); }
                Check(Api.TrustedLogin("https://www.codebuddy.cn/login?state=synthetic"), "Trusted login host");
                foreach (string bad in new[] { "{}", "null", "<html>error</html>", "{\"code\":\"unknown\"}" }) {
                    bool failed = false; try { Api.ParseReply(bad, 200); } catch { failed = true; } Check(failed, "Malformed response must not become success");
                }
                foreach (string bad in new[] { "https://codebuddy.cn.evil.invalid/", "http://www.codebuddy.cn/login", "javascript:alert(1)", "https://user@www.codebuddy.cn/", "https://www.codebuddy.cn:8443/" }) Check(!Api.TrustedLogin(bad), "Reject untrusted login");
                VerifyApi(store).GetAwaiter().GetResult();
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var clean = new MainWindow(true, Path.Combine(temp, "clean")); clean.SavePreview(Path.ChangeExtension(output, ".png")); clean.Close();
                File.WriteAllText(output, "PASS: encrypted storage, blank clean start, 24-hour validation, Beijing scheduling, catch-up, account-specific daily deduplication, trusted authorization URLs, successful/already-done responses, token refresh rotation, auth failure, malformed server response handling, high-DPI UI render.", Encoding.UTF8);
            } catch (Exception ex) { File.WriteAllText(output, "FAIL: " + ex.Message, Encoding.UTF8); }
            finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        }
        static async Task VerifyApi(Store store) {
            using (var api = new Api()) {
                int requests = 0;
                api.Fake = (url, method, headers, ct) => { requests++; return Task.FromResult(new Reply { Code = 0, HttpStatus = 200 }); };
                var a = new Account { AccessToken = "synthetic-access", RefreshToken = "synthetic-refresh", Uid = "synthetic-user" };
                Check((await api.Checkin(a, store, CancellationToken.None)).StartsWith("签到完成"), "Confirmed response");
                Check(requests == 1, "One checkin request");
                api.Fake = (url, method, headers, ct) => Task.FromResult(new Reply { Code = 10001, HttpStatus = 200 });
                Check((await api.Checkin(a, store, CancellationToken.None)).StartsWith("今日已签到"), "Already done response");
                requests = 0;
                api.Fake = (url, method, headers, ct) => {
                    requests++;
                    if (url.EndsWith("/refresh")) return Task.FromResult(new Reply { Code = 0, HttpStatus = 200, Data = Data("accessToken", "rotated-synthetic-access", "refreshToken", "rotated-synthetic-refresh", "expiresIn", 3600) });
                    return Task.FromResult(new Reply { Code = requests == 1 ? 12153 : 0, HttpStatus = requests == 1 ? 401 : 200 });
                };
                await api.Checkin(a, store, CancellationToken.None);
                Check(requests == 3 && store.LoadAccount().RefreshToken == "rotated-synthetic-refresh", "Persist refresh rotation before retry");
                api.Fake = (url, method, headers, ct) => Task.FromResult(new Reply { Code = 12153, HttpStatus = 401 });
                bool refused = false; try { await api.Checkin(a, store, CancellationToken.None); } catch (ServiceException ex) { refused = ex.Auth; } Check(refused, "Auth errors do not become success");
                bool pending = new ServiceException(11217, 200).Pending;
                Check(pending && !new ServiceException(11217, 403).Pending, "Only explicit login pending is polled");
                refused = false; try { await api.Call("https://attacker.invalid/", "POST", null, CancellationToken.None); } catch { refused = true; } Check(refused, "Credentials only reach fixed official hosts");
            }
        }
    }
}
