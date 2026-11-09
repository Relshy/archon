// avatar headshots. the thumbnail api is public, no cookie needed.
// cached to disk so restarts don't refetch, and kept in memory already
// clipped to a circle since doing that per frame would be silly.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace Archon
{
    static class AvatarCache
    {
        static AvatarCache()
        {
            // .net 4 defaults to tls 1.0 and the cdn says no. duplicated in
            // RobloxWeb so neither file depends on the other running first.
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }
        }

        private static readonly object Lock = new object();
        private static readonly Dictionary<long, Image> Memory = new Dictionary<long, Image>();
        private static readonly HashSet<long> Fetching = new HashSet<long>();

        // BeginFetch gets called from the paint handler, so anything that fails
        // permanently (banned account, 404, offline) would retry every single
        // frame. back off instead.
        private static readonly Dictionary<long, Backoff> Failures = new Dictionary<long, Backoff>();

        private class Backoff
        {
            public int Count;
            public DateTime RetryAfterUtc;
        }

        private const double FirstRetrySeconds = 5.0;
        private const double MaxRetrySeconds = 600.0;

        private static string CacheDir
        {
            get { return Path.Combine(AccountStore.Directory, "avatars"); }
        }

        public static Image Get(long userId)
        {
            lock (Lock)
            {
                Image img;
                return Memory.TryGetValue(userId, out img) ? img : null;
            }
        }

        // onReady fires on a pool thread, marshal it yourself before touching ui
        public static void BeginFetch(long userId, Action onReady)
        {
            lock (Lock)
            {
                if (Memory.ContainsKey(userId) || Fetching.Contains(userId)) return;

                Backoff failed;
                if (Failures.TryGetValue(userId, out failed) && DateTime.UtcNow < failed.RetryAfterUtc)
                    return;

                Fetching.Add(userId);
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                Image circular = null;
                try { circular = LoadFromDiskOrNetwork(userId); }
                catch { /* no avatar, the initial letter stays up */ }

                lock (Lock)
                {
                    Fetching.Remove(userId);

                    if (circular != null)
                    {
                        Memory[userId] = circular;
                        Failures.Remove(userId);
                    }
                    else
                    {
                        Backoff failed;
                        if (!Failures.TryGetValue(userId, out failed))
                        {
                            failed = new Backoff();
                            Failures[userId] = failed;
                        }

                        failed.Count++;

                        // 5s, 10s, 20s ... caps at ten minutes
                        double wait = FirstRetrySeconds * Math.Pow(2, Math.Min(failed.Count - 1, 7));
                        if (wait > MaxRetrySeconds) wait = MaxRetrySeconds;
                        failed.RetryAfterUtc = DateTime.UtcNow.AddSeconds(wait);
                    }
                }

                if (circular != null && onReady != null) onReady();
            });
        }

        private static Image LoadFromDiskOrNetwork(long userId)
        {
            string path = Path.Combine(CacheDir, userId.ToString(CultureInfo.InvariantCulture) + ".png");

            try
            {
                if (File.Exists(path) &&
                    (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)) < TimeSpan.FromDays(7))
                {
                    using (Bitmap onDisk = new Bitmap(path))
                        return MakeCircular(onDisk);
                }
            }
            catch { /* corrupt or unreadable cache file - fall through and refetch */ }

            byte[] bytes = Download(userId);
            if (bytes == null) return null;

            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllBytes(path, bytes);
            }
            catch { /* still usable this session even if the cache write fails */ }

            try
            {
                using (MemoryStream ms = new MemoryStream(bytes))
                using (Bitmap raw = new Bitmap(ms))
                    return MakeCircular(raw);
            }
            catch { return null; }
        }

        private static byte[] Download(long userId)
        {
            string listUrl = "https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=" +
                             userId.ToString(CultureInfo.InvariantCulture) +
                             "&size=150x150&format=Png&isCircular=false";

            string json = GetString(listUrl);
            if (json == null) return null;

            Match m = Regex.Match(json, "\"imageUrl\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;

            string imageUrl = m.Groups[1].Value.Replace("\\/", "/");
            return GetBytes(imageUrl);
        }

        private static string GetString(string url)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = "Roblox/WinInet";
            req.Accept = "application/json";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;

            using (HttpWebResponse res = GetResponse(req))
            using (StreamReader reader = new StreamReader(res.GetResponseStream()))
                return reader.ReadToEnd();
        }

        private static byte[] GetBytes(string url)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;

            using (HttpWebResponse res = GetResponse(req))
            using (Stream stream = res.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    ms.Write(buffer, 0, read);
                return ms.ToArray();
            }
        }

        // an error response holds one of the two pooled connections per host
        // until it's disposed - the trap RobloxWeb.WhoAmI describes. a 429 or
        // two from the thumbnail api would otherwise stall every avatar after.
        private static HttpWebResponse GetResponse(HttpWebRequest req)
        {
            try
            {
                return (HttpWebResponse)req.GetResponse();
            }
            catch (WebException wex)
            {
                if (wex.Response != null) wex.Response.Close();
                throw;
            }
        }

        private static Image MakeCircular(Bitmap source)
        {
            int size = Math.Min(source.Width, source.Height);
            Bitmap result = new Bitmap(size, size);

            using (Graphics g = Graphics.FromImage(result))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using (GraphicsPath clip = new GraphicsPath())
                {
                    clip.AddEllipse(0, 0, size, size);
                    g.SetClip(clip);
                    g.DrawImage(source, 0, 0, size, size);
                }
            }

            return result;
        }
    }
}
