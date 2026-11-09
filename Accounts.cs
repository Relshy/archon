// saved sessions + the launch path.
//
// a .ROBLOSECURITY cookie IS the account, no password or 2fa needed, so don't
// ever log one or put it in a window title or an error message. encrypted at
// rest with dpapi (CurrentUser), which protects the file and nothing else -
// anything running as this windows user can still decrypt it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Archon
{
    class RobloxAccount
    {
        public string Username;
        public string DisplayName;
        public long UserId;
        public DateTime AddedUtc;

        // dpapi blob, base64. never the plain cookie.
        public string ProtectedCookie;

        public string Label
        {
            get { return string.IsNullOrEmpty(DisplayName) ? Username : DisplayName; }
        }
    }

    static class AccountStore
    {
        // not a secret, just stops random other code that calls dpapi from
        // being able to decrypt our blobs
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Archon/v1/accounts");

        private const string Header = "ARCHONv1";

        public static string Directory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Archon");
            }
        }

        public static string FilePath
        {
            get { return Path.Combine(Directory, "accounts.dat"); }
        }

        public static string Protect(string plain)
        {
            byte[] data = Encoding.UTF8.GetBytes(plain);
            try
            {
                byte[] blob = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(blob);
            }
            finally
            {
                Array.Clear(data, 0, data.Length);
            }
        }

        public static string Unprotect(string protectedBase64)
        {
            byte[] blob = Convert.FromBase64String(protectedBase64);
            byte[] data = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(data);
            }
            finally
            {
                Array.Clear(data, 0, data.Length);
            }
        }

        public static List<RobloxAccount> Load()
        {
            List<RobloxAccount> accounts = new List<RobloxAccount>();
            string path = FilePath;
            if (!File.Exists(path)) return accounts;

            // a failed read throws rather than coming back empty. empty looks
            // like "no saved accounts", and the next save would then write
            // that over the real file. same av-scanner lock as in Save.
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line) || line == Header) continue;

                string[] parts = line.Split('\t');
                if (parts.Length < 5) continue;

                RobloxAccount a = new RobloxAccount();
                a.Username = parts[0];

                long id;
                long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
                a.UserId = id;

                a.DisplayName = parts[2];

                // catch a corrupt blob now, otherwise it blows up at launch
                // time and looks like the launch is broken instead of the entry
                if (!IsBase64(parts[3])) continue;
                a.ProtectedCookie = parts[3];

                long ticks;
                if (long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks))
                {
                    try { a.AddedUtc = new DateTime(ticks, DateTimeKind.Utc); }
                    catch { a.AddedUtc = DateTime.UtcNow; }
                }

                accounts.Add(a);
            }

            return accounts;
        }

        public static void Save(List<RobloxAccount> accounts)
        {
            System.IO.Directory.CreateDirectory(Directory);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Header);
            foreach (RobloxAccount a in accounts)
            {
                sb.Append(Clean(a.Username)).Append('\t');
                sb.Append(a.UserId.ToString(CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(Clean(a.DisplayName)).Append('\t');
                sb.Append(a.ProtectedCookie).Append('\t');
                sb.Append(a.AddedUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
            }

            // File.Replace, NOT delete then move. delete-then-move leaves a gap
            // where the file doesn't exist, and if the move then fails (av
            // scanner holding it for a second is enough) every saved account is
            // gone for good.
            string path = FilePath;
            string temp = path + ".tmp";
            File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));

            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        private static bool IsBase64(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            try { Convert.FromBase64String(s); return true; }
            catch (FormatException) { return false; }
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }

    class RobloxIdentity
    {
        public long UserId;
        public string Username;
        public string DisplayName;
    }

    // the two web calls we need: check a cookie, and swap it for a ticket
    static class RobloxWeb
    {
        static RobloxWeb()
        {
            // .net 4 defaults to tls 1.0, roblox says no. numbers instead of
            // the enum names so it still compiles against the 4.0 refs.
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }
            ServicePointManager.Expect100Continue = false;
        }

        private const string UserAgent = "Roblox/WinInet";

        private static HttpWebRequest Build(string url, string method, string cookie)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = UserAgent;
            req.Accept = "application/json";
            req.Referer = "https://www.roblox.com/";
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            // raw header, not CookieContainer - the cookie has chars the
            // Cookie class refuses to accept
            req.Headers["Cookie"] = ".ROBLOSECURITY=" + cookie;
            return req;
        }

        public static RobloxIdentity WhoAmI(string cookie, out string error)
        {
            error = null;
            try
            {
                HttpWebRequest req = Build("https://users.roblox.com/v1/users/authenticated", "GET", cookie);
                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
                using (StreamReader reader = new StreamReader(res.GetResponseStream()))
                {
                    string body = reader.ReadToEnd();

                    RobloxIdentity id = new RobloxIdentity();
                    id.UserId = ReadLong(body, "id");
                    id.Username = ReadString(body, "name");
                    id.DisplayName = ReadString(body, "displayName");

                    if (id.UserId == 0 || string.IsNullOrEmpty(id.Username))
                    {
                        error = "Roblox did not return an account for that cookie.";
                        return null;
                    }
                    return id;
                }
            }
            catch (WebException wex)
            {
                // dispose it. an error response still holds a pooled connection
                // and .net only allows two per host, so leaking a few of these
                // starves the pool and the next request just sits there until
                // it times out. shows up as launching randomly hanging.
                using (WebResponse raw = wex.Response)
                {
                    HttpWebResponse res = raw as HttpWebResponse;
                    if (res != null && res.StatusCode == HttpStatusCode.Unauthorized)
                        error = "That cookie is not valid or has expired. Log in again and copy a fresh one.";
                    else
                        error = Describe(wex, res);
                    return null;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        // swaps the cookie for a one-shot ticket. the first post is meant to
        // 403 - that's how you get the csrf token for the real one.
        public static string GetAuthTicket(string cookie, out string error)
        {
            error = null;

            string csrf = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    HttpWebRequest req = Build("https://auth.roblox.com/v1/authentication-ticket/", "POST", cookie);
                    req.ContentType = "application/json";
                    req.ContentLength = 0;
                    if (csrf != null) req.Headers["X-CSRF-TOKEN"] = csrf;

                    using (req.GetRequestStream()) { }

                    using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
                    {
                        string ticket = res.Headers["rbx-authentication-ticket"];
                        if (string.IsNullOrEmpty(ticket))
                        {
                            error = "Roblox accepted the request but returned no ticket.";
                            return null;
                        }
                        return ticket;
                    }
                }
                catch (WebException wex)
                {
                    // disposed for the same reason as in GetIdentity
                    bool retryWithToken = false;

                    using (WebResponse raw = wex.Response)
                    {
                        HttpWebResponse res = raw as HttpWebResponse;

                        if (res != null && (int)res.StatusCode == 403 && csrf == null)
                        {
                            csrf = res.Headers["x-csrf-token"];
                            if (!string.IsNullOrEmpty(csrf)) retryWithToken = true;
                            else
                            {
                                error = "Roblox rejected the request and gave no CSRF token.";
                                return null;
                            }
                        }
                        else if (res != null && res.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            error = "That cookie has expired. Re-add the account with a fresh cookie.";
                        }
                        else
                        {
                            error = Describe(wex, res);
                        }
                    }

                    if (retryWithToken) continue;
                    return null;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return null;
                }
            }

            error = "Could not obtain an authentication ticket.";
            return null;
        }

        // builds the roblox-player:// url. this format is roblox's and they
        // change it now and then, so if launching breaks after an update start
        // looking here.
        public static string BuildLaunchUrl(string ticket, long placeId, string code, bool isLinkCode)
        {
            // mask, not Math.Abs - Math.Abs(int.MinValue) throws
            long trackerId = 100000 + ((Guid.NewGuid().GetHashCode() & int.MaxValue) % 900000);
            long launchTime = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

            StringBuilder placeLauncher = new StringBuilder();
            placeLauncher.Append("https://assetgame.roblox.com/game/PlaceLauncher.ashx");

            if (!string.IsNullOrEmpty(code))
            {
                // one or the other, never both. get it wrong and PlaceLauncher
                // quietly drops you in a public server.
                placeLauncher.Append("?request=RequestPrivateGame");
                placeLauncher.Append("&browserTrackerId=").Append(trackerId.ToString(CultureInfo.InvariantCulture));
                placeLauncher.Append("&placeId=").Append(placeId.ToString(CultureInfo.InvariantCulture));
                placeLauncher.Append(isLinkCode ? "&linkCode=" : "&accessCode=")
                             .Append(Escape(code));
            }
            else
            {
                placeLauncher.Append("?request=RequestGame");
                placeLauncher.Append("&browserTrackerId=").Append(trackerId.ToString(CultureInfo.InvariantCulture));
                placeLauncher.Append("&placeId=").Append(placeId.ToString(CultureInfo.InvariantCulture));
                placeLauncher.Append("&isPlayTogetherGame=false");
            }

            StringBuilder url = new StringBuilder();
            url.Append("roblox-player:1");
            url.Append("+launchmode:play");
            url.Append("+gameinfo:").Append(ticket);
            url.Append("+launchtime:").Append(launchTime.ToString(CultureInfo.InvariantCulture));
            url.Append("+placelauncherurl:").Append(Uri.EscapeDataString(placeLauncher.ToString()));
            url.Append("+browsertrackerid:").Append(trackerId.ToString(CultureInfo.InvariantCulture));
            url.Append("+robloxLocale:en_us");
            url.Append("+gameLocale:en_us");
            url.Append("+channel:");
            return url.ToString();
        }

        // handles the three things people actually paste: a full private server
        // link, a plain game link (place id only), or just the bare code.
        public static bool TryParsePrivateServerLink(string input, out long placeId,
                                                     out string code, out bool isLinkCode)
        {
            placeId = 0;
            code = null;
            isLinkCode = false;
            if (string.IsNullOrEmpty(input)) return false;

            string s = input.Trim();

            if (s.IndexOf("roblox.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Match placeMatch = Regex.Match(s, "/games/(\\d+)");
                if (placeMatch.Success)
                    long.TryParse(placeMatch.Groups[1].Value, NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out placeId);

                // share links use privateServerLinkCode, old vip urls use
                // accessCode. not interchangeable, different param each.
                Match linkMatch = Regex.Match(s, "[?&]privateServerLinkCode=([^&\\s]+)", RegexOptions.IgnoreCase);
                if (linkMatch.Success)
                {
                    code = Uri.UnescapeDataString(linkMatch.Groups[1].Value);
                    isLinkCode = true;
                }
                else
                {
                    Match accessMatch = Regex.Match(s, "[?&]accessCode=([^&\\s]+)", RegexOptions.IgnoreCase);
                    if (accessMatch.Success)
                        code = Uri.UnescapeDataString(accessMatch.Groups[1].Value);
                }

                return placeId > 0 || !string.IsNullOrEmpty(code);
            }

            // bare code. only the two shapes roblox actually issues, otherwise
            // a typo in the box gets sent as a "code" and you end up public.
            if (Regex.IsMatch(s, "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"))
            {
                code = s;              // VIP server accessCode
                isLinkCode = false;
                return true;
            }

            if (Regex.IsMatch(s, "^[0-9A-Za-z_-]{16,64}$") && s.IndexOf('-') < 0)
            {
                code = s;              // private server linkCode
                isLinkCode = true;
                return true;
            }

            code = null;
            return false;
        }

        public static void Launch(string launchUrl)
        {
            Process.Start(launchUrl);
        }

        // EscapeDataString throws past ~32k chars. nothing gets that long right
        // now but this is on the launch path so a throw costs the launch.
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Length > 30000) value = value.Substring(0, 30000);
            return Uri.EscapeDataString(value);
        }

        // takes the response instead of reading it back off the exception, so
        // the caller's using block owns disposing it
        private static string Describe(WebException wex, HttpWebResponse res)
        {
            if (res == null) return wex.Message;

            string detail = null;
            try
            {
                using (StreamReader reader = new StreamReader(res.GetResponseStream()))
                {
                    string body = reader.ReadToEnd();
                    detail = ReadString(body, "message");
                }
            }
            catch { }

            string status = (int)res.StatusCode + " " + res.StatusCode;
            return string.IsNullOrEmpty(detail) ? ("Roblox returned " + status) : (status + ": " + detail);
        }

        // the responses we care about have like four flat fields each, not
        // worth dragging in a json library
        private static long ReadLong(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)");
            if (!m.Success) return 0;
            long value;
            long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            return value;
        }

        private static string ReadString(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            return m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\/", "/");
        }
    }
}
