using Perforce.P4;
using Perforce.P4Scm;
using Perforce.P4VS;
using System;
using System.Collections.Generic;

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
            Options clOpts = new Options();
            clOpts["-m"] = "2000";
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
                        info.User = cl.OwnerName ?? string.Empty;
                        info.Date = cl.ModifiedDate;
                        info.Description = cl.Description ?? string.Empty;
                    }

                    result.Add(info);
                }
            }

            return result;
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
