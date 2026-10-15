using Perforce.P4;
using Perforce.P4Scm;
using Perforce.P4VS;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CodeLensOopProviderVsix.Blame
{
    /// <summary>
    /// Blame data for a single source line: the changelist that last touched it,
    /// plus that changelist's metadata.
    /// </summary>
    internal sealed class BlameLineInfo
    {
        public int ChangeNumber = -1;
        public string User = string.Empty;
        public DateTime? Date;
        public string Description = string.Empty;
    }

    /// <summary>
    /// Fetches per-line annotate data through the live P4VS connection.
    /// Must be called off the UI thread (blocking P4 server round trips).
    /// </summary>
    internal static class P4BlameService
    {
        public static IList<BlameLineInfo> GetBlame(string filePath)
        {
            P4ScmProvider scm = P4VsProvider.CurrentScm;
            if (scm == null || !scm.CheckConnection())
            {
                throw new InvalidOperationException(
                    "Not connected to P4. Connect first (P4 menu > Connect), then retry.");
            }

            Repository rep = scm.Connection.Repository;

            // p4 annotate -c : per-line last-changed changelist number.
            Options annotateOpts = new Options();
            annotateOpts["-c"] = null;
            IList<FileAnnotation> annotations = rep.GetFileAnnotations(
                new List<FileSpec> { new FileSpec(new LocalPath(filePath), null) },
                annotateOpts);

            // Changelist id -> metadata (user / date / description). p4 annotate's tagged
            // output carries no user/date, so build a lookup from the file's history.
            // -l: full descriptions. The default output truncates to the first 31 chars,
            // which would cut "#auto-merge ... by <user>" before the name and break the
            // auto-merge author extraction below.
            Options clOpts = new Options();
            clOpts["-m"] = "2000";
            clOpts["-l"] = null;
            IList<Changelist> changelists = rep.GetChangelists(
                clOpts, new FileSpec(new LocalPath(filePath), null));

            Dictionary<int, Changelist> byId = new Dictionary<int, Changelist>();
            if (changelists != null)
            {
                foreach (Changelist cl in changelists)
                {
                    if (cl != null)
                    {
                        byId[cl.Id] = cl;
                    }
                }
            }

            List<BlameLineInfo> result = new List<BlameLineInfo>(annotations?.Count ?? 0);
            if (annotations != null)
            {
                foreach (FileAnnotation a in annotations)
                {
                    BlameLineInfo info = new BlameLineInfo();
                    info.ChangeNumber = ExtractChangeNumber(a);

                    Changelist cl;
                    if (info.ChangeNumber > 0 && byId.TryGetValue(info.ChangeNumber, out cl))
                    {
                        info.Description = cl.Description ?? string.Empty;
                        info.User = ExtractDisplayUser(cl.OwnerName, info.Description);
                        info.Date = cl.ModifiedDate;
                    }

                    result.Add(info);
                }
            }

            return result;
        }

        private const string AutoMergeMarker = "#auto-merge";

        // Only applied to the line containing AutoMergeMarker (cheap IndexOf gate first).
        // Captures the user name after "by " at end of that line, e.g.:
        //   "#auto-merge:manual Rel-0.2->MainDev CL:117018 by ShenShuo"  -> ShenShuo
        //   "#auto-merge: Rel-0.2->MainDev CL:117069 by SuJiawei"        -> SuJiawei
        private static readonly Regex AutoMergeUserRegex = new Regex(
            @"\bby\s+(?<user>[A-Za-z0-9_.\-]+)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string ExtractDisplayUser(string ownerName, string description)
        {
            if (string.IsNullOrEmpty(description))
            {
                return ownerName ?? string.Empty;
            }

            // Step 1: cheap marker search; most CLs return here without regex.
            int markerIndex = description.IndexOf(AutoMergeMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return ownerName ?? string.Empty;
            }

            // Step 2: isolate the marker's own line (the description may carry extra
            // lines below it), then extract "by <user>" at that line's end.
            int lineStart = description.LastIndexOf('\n', markerIndex) + 1;
            int lineEnd = description.IndexOf('\n', markerIndex);
            if (lineEnd < 0)
            {
                lineEnd = description.Length;
            }

            string markerLine = description.Substring(lineStart, lineEnd - lineStart);

            Match m = AutoMergeUserRegex.Match(markerLine);
            if (m.Success)
            {
                return m.Groups["user"].Value;
            }

            return ownerName ?? string.Empty;
        }

        private static int ExtractChangeNumber(FileAnnotation a)
        {
            try
            {
                VersionRange range = a?.File?.Version as VersionRange;
                VersionSpec spec = range != null ? range.Lower : a?.File?.Version;

                // Note: property name typo "ChanglistId" exists in p4api.net.
                ChangelistIdVersion civ = spec as ChangelistIdVersion;
                if (civ != null)
                {
                    return civ.ChanglistId;
                }

                // If p4api.net failed to detect the -c flag the version is wrapped as a
                // Revision, but the number still IS the changelist id (we passed -c).
                Revision rev = spec as Revision;
                if (rev != null)
                {
                    return rev.Rev;
                }
            }
            catch
            {
                // fall through -> unknown
            }

            return -1;
        }
    }
}
