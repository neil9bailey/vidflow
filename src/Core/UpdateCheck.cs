using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security;
using System.Text;
using System.Threading;

namespace VidFlow.Core
{
    public enum UpdateStatus { UpToDate, Available, NoReleases, Failed }

    // Outcome of one "Check for Updates" request.
    public sealed class UpdateResult
    {
        public UpdateStatus Status { get; set; }
        public Version Current { get; set; }        // this build (major.minor.build)
        public Version Latest { get; set; }         // the latest release (null unless parsed)
        public string Tag { get; set; }             // "v1.2.3" as published
        public string ReleaseName { get; set; }
        public string PageUrl { get; set; }         // release page to open (always https://github.com/...)
        public string Error { get; set; }           // Failed: what went wrong, for the user
        public int HttpStatus { get; set; }         // 0 = no HTTP response
    }

    // "Check for Updates": one GET of GitHub's latest-release API for Settings.UpdateRepo, made
    // only when the user asks for it - nothing here runs in the background or at startup.
    // VIDFLOW_UPDATE_API replaces https://api.github.com (tests point it at a local server).
    public static class UpdateCheck
    {
        public const string ApiBaseVariable = "VIDFLOW_UPDATE_API";
        private const string DefaultApiBase = "https://api.github.com";
        private const int TimeoutMs = 10000;
        private const int MaxBodyBytes = 1 << 20;

        public static string ApiBase
        {
            get
            {
                string v = Environment.GetEnvironmentVariable(ApiBaseVariable);
                Uri u;
                if (!string.IsNullOrWhiteSpace(v) && Uri.TryCreate(v.Trim(), UriKind.Absolute, out u) &&
                    (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                    return v.Trim().TrimEnd('/');
                return DefaultApiBase;
            }
        }

        // "owner/repo" made of the characters GitHub allows in account and repository names.
        public static bool IsValidRepo(string repo)
        {
            string s = (repo ?? "").Trim();
            int slash = s.IndexOf('/');
            if (slash <= 0 || slash != s.LastIndexOf('/') || slash == s.Length - 1 || s.Length > 140) return false;
            foreach (char c in s)
                if (!(c == '/' || c == '-' || c == '_' || c == '.' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
                    return false;
            string name = s.Substring(slash + 1);
            return name != "." && name != "..";
        }

        public static string LatestReleaseApiUrl(string repo)
        {
            return ApiBase + "/repos/" + repo.Trim() + "/releases/latest";
        }

        // The repository's latest-release page (fallback when the API's link is not a GitHub page).
        public static string ReleasesPageUrl(string repo)
        {
            return "https://github.com/" + repo.Trim() + "/releases/latest";
        }

        // 1.0.0.0 -> 1.0.0 (the revision is never part of a release tag).
        public static Version Normalize(Version v)
        {
            if (v == null) return new Version(0, 0, 0);
            return new Version(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));
        }

        public static string Display(Version v)
        {
            v = Normalize(v);
            return v.Major.ToString(CultureInfo.InvariantCulture) + "." + v.Minor.ToString(CultureInfo.InvariantCulture) + "." +
                   v.Build.ToString(CultureInfo.InvariantCulture);
        }

        // "v1.2.3", "1.2", "V2.0.1-beta.2", "release-1.4" -> major.minor.build; false without digits
        // or with a major above 999.
        public static bool TryParseVersion(string tag, out Version version)
        {
            version = null;
            string s = (tag ?? "").Trim();
            int start = 0;
            while (start < s.Length && !char.IsDigit(s[start])) start++;
            if (start == s.Length) return false;
            var parts = new List<int>();
            int i = start;
            while (i < s.Length && parts.Count < 3)
            {
                int j = i;
                while (j < s.Length && char.IsDigit(s[j])) j++;
                int n;
                if (j == i || j - i > 9 || !int.TryParse(s.Substring(i, j - i), NumberStyles.None, CultureInfo.InvariantCulture, out n)) break;
                parts.Add(n);
                if (j >= s.Length || s[j] != '.') break;
                i = j + 1;
            }
            // A date-stamped tag (the engine mirror's "engine-20261010") is not a version.
            if (parts.Count == 0 || parts[0] > 999) return false;
            while (parts.Count < 3) parts.Add(0);
            version = new Version(parts[0], parts[1], parts[2]);
            return true;
        }

        // Runs Check on a background thread and hands the result to done on that thread.
        public static void CheckAsync(string repo, string product, Version current, Action<UpdateResult> done)
        {
            if (done == null) throw new ArgumentNullException("done");
            var t = new Thread(() =>
            {
                UpdateResult r;
                try
                {
                    r = Check(repo, product, current);
                }
                catch (Exception ex)
                {
                    // Nothing may escape a worker thread (it would end the process).
                    Log.Error("update check failed", ex);
                    r = Failed(current, "Unexpected error: " + ex.Message, 0);
                }
                done(r);
            });
            t.IsBackground = true;
            t.Name = "VidFlow update check";
            t.Start();
        }

        // Synchronous request (blocks for up to ~10 s): call it off the UI thread.
        public static UpdateResult Check(string repo, string product, Version current)
        {
            current = Normalize(current);
            if (!IsValidRepo(repo)) return Failed(current, "No update repository is configured.", 0);
            string url = LatestReleaseApiUrl(repo);
            EnsureTls12();
            HttpWebRequest req;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(url);
            }
            catch (Exception ex) when (ex is UriFormatException || ex is NotSupportedException || ex is SecurityException || ex is InvalidCastException)
            {
                return Failed(current, "Invalid update address (" + ex.Message + ").", 0);
            }
            req.Method = "GET";
            // An HTTP product token has no spaces: "VidFlow Player" goes out as "VidFlow".
            string token = string.IsNullOrEmpty(product) ? "VidFlow" : product.Split(' ')[0];
            req.UserAgent = token + "/" + Display(current) + " (update check)";
            req.Accept = "application/vnd.github+json";
            req.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            req.Timeout = TimeoutMs;
            req.ReadWriteTimeout = TimeoutMs;
            req.KeepAlive = false;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            // Timeout does not cover name resolution; a watchdog aborts the whole request at 10 s.
            int timedOut = 0;
            using (new Timer(_ => { Interlocked.Exchange(ref timedOut, 1); req.Abort(); }, null, TimeoutMs, Timeout.Infinite))
            {
                try
                {
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        string body = ReadBody(resp);
                        Log.Info("update check: HTTP " + (int)resp.StatusCode + " from " + url);
                        return Evaluate(body, current, repo, (int)resp.StatusCode);
                    }
                }
                catch (WebException ex)
                {
                    var resp = ex.Response as HttpWebResponse;
                    int code = resp != null ? (int)resp.StatusCode : 0;
                    string rateLeft = resp != null ? resp.Headers["X-RateLimit-Remaining"] : null;
                    if (resp != null) resp.Close();
                    Log.Warn("update check: " + ex.Status + " " + code + " from " + url + ": " + ex.Message);
                    if (code == 404) return new UpdateResult { Status = UpdateStatus.NoReleases, Current = current, HttpStatus = code, PageUrl = ReleasesPageUrl(repo) };
                    if (code == 403 || code == 429)
                        return Failed(current, rateLeft == "0" ? "GitHub's request limit was reached. Try again in an hour." : "GitHub refused the request (HTTP " + code + ").", code);
                    if (code != 0) return Failed(current, "GitHub answered HTTP " + code + ".", code);
                    if (Interlocked.CompareExchange(ref timedOut, 0, 0) == 1 || ex.Status == WebExceptionStatus.Timeout)
                        return Failed(current, "No answer within " + (TimeoutMs / 1000) + " seconds.", 0);
                    if (ex.Status == WebExceptionStatus.NameResolutionFailure || ex.Status == WebExceptionStatus.ConnectFailure)
                        return Failed(current, "Could not connect to " + HostOf(url) + ". Check your internet connection.", 0);
                    return Failed(current, ex.Message, 0);
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is NotSupportedException || ex is ProtocolViolationException)
                {
                    Log.Warn("update check: " + ex.Message);
                    return Interlocked.CompareExchange(ref timedOut, 0, 0) == 1
                        ? Failed(current, "No answer within " + (TimeoutMs / 1000) + " seconds.", 0)
                        : Failed(current, ex.Message, 0);
                }
            }
        }

        // Interprets a releases/latest response body (separate from the request for testing).
        public static UpdateResult Evaluate(string json, Version current, string repo, int httpStatus)
        {
            current = Normalize(current);
            Dictionary<string, object> release;
            try
            {
                release = MiniJson.Parse(json) as Dictionary<string, object>;
            }
            catch (FormatException ex)
            {
                return Failed(current, "Unreadable answer from GitHub (" + ex.Message + ").", httpStatus);
            }
            if (release == null) return Failed(current, "Unreadable answer from GitHub.", httpStatus);
            string tag = Str(release, "tag_name");
            Version latest;
            if (!TryParseVersion(tag, out latest)) return Failed(current, "The latest release has no version number" + (string.IsNullOrEmpty(tag) ? "." : " (\"" + tag + "\")."), httpStatus);
            // A final release beats a pre-release of the same number (1.0.0 > 1.0.0-beta.3).
            bool newer = latest > current
                || (latest == current && AppInfo.Version.IndexOf('-') >= 0 && tag.TrimStart('v', 'V').IndexOf('-') < 0);
            return new UpdateResult
            {
                Status = newer ? UpdateStatus.Available : UpdateStatus.UpToDate,
                Current = current,
                Latest = latest,
                Tag = tag,
                ReleaseName = Str(release, "name"),
                PageUrl = SafePageUrl(Str(release, "html_url"), repo),
                HttpStatus = httpStatus,
            };
        }

        // Only a github.com https page is ever handed to the shell; anything else from the
        // network is replaced by the repository's own releases page.
        public static string SafePageUrl(string url, string repo)
        {
            Uri u;
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out u) && u.Scheme == Uri.UriSchemeHttps &&
                string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase) && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo))
                return u.AbsoluteUri;
            return IsValidRepo(repo) ? ReleasesPageUrl(repo) : null;
        }

        private static UpdateResult Failed(Version current, string error, int httpStatus)
        {
            return new UpdateResult { Status = UpdateStatus.Failed, Current = Normalize(current), Error = error, HttpStatus = httpStatus };
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v as string : null;
        }

        private static string HostOf(string url)
        {
            Uri u;
            return Uri.TryCreate(url, UriKind.Absolute, out u) ? u.Host : url;
        }

        // .NET Framework only offers TLS 1.0 to apps without a target-framework opt-in, and
        // GitHub requires TLS 1.2. "SystemDefault" (0) already lets Windows pick 1.2/1.3.
        private static void EnsureTls12()
        {
            var p = ServicePointManager.SecurityProtocol;
            if (p != SecurityProtocolType.SystemDefault && (p & SecurityProtocolType.Tls12) == 0)
                ServicePointManager.SecurityProtocol = p | SecurityProtocolType.Tls12;
        }

        private static string ReadBody(HttpWebResponse resp)
        {
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                if (s == null) return "";
                var buf = new byte[16384];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ms.Length + n > MaxBodyBytes) throw new IOException("the answer is larger than " + (MaxBodyBytes >> 10) + " KB");
                    ms.Write(buf, 0, n);
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        // Minimal JSON reader for the release object: objects -> Dictionary<string, object>,
        // arrays -> List<object>, strings, doubles, bools and null. Throws FormatException.
        private sealed class MiniJson
        {
            private readonly string _s;
            private int _i;
            private int _depth;

            private MiniJson(string s) { _s = s ?? ""; }

            public static object Parse(string s)
            {
                var p = new MiniJson(s);
                p.SkipWs();
                object v = p.Value();
                p.SkipWs();
                if (p._i != p._s.Length) throw p.Error("unexpected text after the value");
                return v;
            }

            private FormatException Error(string what)
            {
                return new FormatException(what + " at offset " + _i.ToString(CultureInfo.InvariantCulture));
            }

            private void SkipWs()
            {
                while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\r' || _s[_i] == '\n' || _s[_i] == '\uFEFF')) _i++;
            }

            private object Value()
            {
                if (_i >= _s.Length) throw Error("unexpected end");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return Obj();
                    case '[': return Arr();
                    case '"': return Str();
                    case 't': Word("true"); return true;
                    case 'f': Word("false"); return false;
                    case 'n': Word("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return Number();
                        throw Error("unexpected '" + c + "'");
                }
            }

            private void Word(string w)
            {
                if (string.CompareOrdinal(_s, _i, w, 0, w.Length) != 0) throw Error("expected " + w);
                _i += w.Length;
            }

            private void Enter()
            {
                if (++_depth > 64) throw Error("nested too deeply");
                _i++;
                SkipWs();
            }

            private Dictionary<string, object> Obj()
            {
                Enter();
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                if (_i < _s.Length && _s[_i] == '}') { _i++; _depth--; return d; }
                while (true)
                {
                    if (_i >= _s.Length || _s[_i] != '"') throw Error("expected a key");
                    string key = Str();
                    SkipWs();
                    if (_i >= _s.Length || _s[_i] != ':') throw Error("expected ':'");
                    _i++;
                    SkipWs();
                    d[key] = Value();
                    SkipWs();
                    if (_i < _s.Length && _s[_i] == ',') { _i++; SkipWs(); continue; }
                    if (_i < _s.Length && _s[_i] == '}') { _i++; _depth--; return d; }
                    throw Error("expected ',' or '}'");
                }
            }

            private List<object> Arr()
            {
                Enter();
                var l = new List<object>();
                if (_i < _s.Length && _s[_i] == ']') { _i++; _depth--; return l; }
                while (true)
                {
                    l.Add(Value());
                    SkipWs();
                    if (_i < _s.Length && _s[_i] == ',') { _i++; SkipWs(); continue; }
                    if (_i < _s.Length && _s[_i] == ']') { _i++; _depth--; return l; }
                    throw Error("expected ',' or ']'");
                }
            }

            private string Str()
            {
                _i++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length) throw Error("unterminated string");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (_i >= _s.Length) throw Error("unterminated string");
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                        {
                            int code;
                            if (_i + 4 > _s.Length || !int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                throw Error("bad \\u escape");
                            sb.Append((char)code);
                            _i += 4;
                            break;
                        }
                        default: throw Error("bad escape");
                    }
                }
            }

            private double Number()
            {
                int start = _i;
                if (_s[_i] == '-') _i++;
                while (_i < _s.Length && ((_s[_i] >= '0' && _s[_i] <= '9') || _s[_i] == '.' || _s[_i] == 'e' || _s[_i] == 'E' || _s[_i] == '+' || _s[_i] == '-')) _i++;
                double v;
                if (!double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) throw Error("bad number");
                return v;
            }
        }
    }
}
