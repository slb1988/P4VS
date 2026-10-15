extern alias EnvDteX;
extern alias VsCmdBars;

using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.ComponentModel.Design;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using IServiceProvider = System.IServiceProvider;
using Task = System.Threading.Tasks.Task;
using Perforce.P4VS;
using Perforce.P4Scm;

namespace CodeLensOopProviderVsix
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the
    /// IVsPackage interface and uses the registration attributes defined in the framework to
    /// register itself and its components with the shell. These attributes tell the pkgdef creation
    /// utility what data to put into .pkgdef file.
    /// </para>
    /// <para>
    /// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
    /// </para>
    /// </remarks>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)] // Info on this package for Help/About
    [ProvideMenuResource("Menus.ctmenu", 4)]
    [Guid(GuidAndCmdID.PackageGuidString)]
    // UIContextGuids80.CodeWindow: load in the background as soon as a code window opens,
    // so the "Toggle P4 Blame" editor context-menu entry is always handled.
    [ProvideAutoLoad("8FE2DF1D-E0DA-4EBE-9D5C-415D40AD4874", PackageAutoLoadFlags.BackgroundLoad)]
    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1650:ElementDocumentationMustBeSpelledCorrectly", Justification = "pkgdef, VS and vsixmanifest are valid VS terms")]
    [ProvideBindingPath]
    public sealed class CodeLensOopProviderPackage : AsyncPackage, IOleCommandTarget
    {
        private IOleCommandTarget pkgCommandTarget;

        /// <summary>
        /// Initializes a new instance of the <see cref="CodeLensOopProviderPackage"/> class.
        /// </summary>
        public CodeLensOopProviderPackage()
        {
            //System.Windows.Forms.MessageBox.Show("CodeLensOopProviderPackage");
            // Inside this method you can place any initialization code that does not require
            // any Visual Studio service because at this point the package object is created but
            // not sited yet inside Visual Studio environment. The place to do all the other
            // initialization is the Initialize method.
        }

        #region Package Members

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to monitor for initialization cancellation, which can occur when VS is shutting down.</param>
        /// <param name="progress">A provider for progress updates.</param>
        /// <returns>A task representing the async work of package initialization, or an already completed task if there is none. Do not return null from this method.</returns>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            //System.Windows.Forms.MessageBox.Show("InitializeAsync");

            await base.InitializeAsync(cancellationToken, progress);

            // When initialized asynchronously, the current thread may be a background thread at this point.
            // Do any initialization that requires the UI thread after switching to the UI thread.
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return;

            this.pkgCommandTarget = await this.GetServiceAsync(typeof(IOleCommandTarget)) as IOleCommandTarget;

            if (cancellationToken.IsCancellationRequested)
                return;

            OleMenuCommandService commandService = await this.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
            {
                CommandID blameCommandId = new CommandID(GuidAndCmdID.guidCmdSet, (int)GuidAndCmdID.cmdidToggleBlame);
                OleMenuCommand blameCommand = new OleMenuCommand(OnToggleBlameExecute, null, OnToggleBlameBeforeQueryStatus, blameCommandId);
                commandService.AddCommand(blameCommand);
            }

            Instance = this;
            EnsureJsonContextMenuBlameButton("load");
        }

        #endregion

        #region JSON editor context menu injection

        // VSCT group->menu merge into the WebTools JSON editor context menu ({F718CA06-CF4F-4A0C-9106-E79E9EE5E7CD}:1)
        // does NOT work (2026-10-15 verified: merge never materializes, item invisible no matter what).
        // .uproject/.uplugin/.json all open in that editor (its factory claims "*" as fallback), and its
        // right-click menu is not IDM_VS_CTXT_CODEWIN, so without this the command is unreachable there.
        // Copilot/Git entries get in dynamically; we do the same: Command.AddControl onto the "JSON Context"
        // command bar (bar name is locale-invariant). Purely add-if-missing: deleting/re-adding controls
        // while the bar is still populating corrupts its state (2026-10-15, v2026.1.5.0 regression).
        // Position is NOT enforced (user decision) - a load-time add can land at the top.
        // Triggers: package load, our own QueryStatus, and BlameMarginProvider.CreateMargin (fires exactly
        // when a JSON-ish editor opens, which is the chicken-and-egg case: our QueryStatus alone never
        // runs from a menu that doesn't contain our command yet).
        private bool jsonMenuEnsured;

        internal static CodeLensOopProviderPackage Instance { get; private set; }

        private static void BlameMenuLog(string message)
        {
            try
            {
                System.IO.File.AppendAllText(@"C:\Windows\Temp\p4vs_blame_menu.log",
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch (Exception) { }
        }

        internal void EnsureJsonContextMenuBlameButton(string trigger)
        {
            if (jsonMenuEnsured)
                return;
            if (!ThreadHelper.CheckAccess())
            {
                BlameMenuLog(trigger + ": skip (not UI thread)");
                return; // retried on a later trigger
            }

            EnvDteX::EnvDTE.DTE dte = GetService(typeof(SDTE)) as EnvDteX::EnvDTE.DTE;
            if (dte == null)
            {
                BlameMenuLog(trigger + ": DTE null");
                return;
            }

            try
            {
                VsCmdBars::Microsoft.VisualStudio.CommandBars.CommandBars bars =
                    dte.CommandBars as VsCmdBars::Microsoft.VisualStudio.CommandBars.CommandBars;
                if (bars == null)
                {
                    BlameMenuLog(trigger + ": CommandBars null");
                    return;
                }

                VsCmdBars::Microsoft.VisualStudio.CommandBars.CommandBar bar = bars["JSON Context"]; // throws if JSON editor package not ready yet; retried lazily
                if (bar == null)
                {
                    BlameMenuLog(trigger + ": bar null");
                    return;
                }

                foreach (VsCmdBars::Microsoft.VisualStudio.CommandBars.CommandBarControl ctl in bar.Controls)
                {
                    string caption = null;
                    try { caption = ctl.Caption; } catch (Exception) { }
                    if (caption == "Toggle P4 Blame")
                    {
                        jsonMenuEnsured = true;
                        BlameMenuLog(trigger + ": already present (idx>0), count=" + bar.Controls.Count);
                        return;
                    }
                }

                EnvDteX::EnvDTE.Command cmd = dte.Commands.Item("{" + GuidAndCmdID.PackageCmdSetGuidString + "}", (int)GuidAndCmdID.cmdidToggleBlame);
                if (cmd == null)
                {
                    BlameMenuLog(trigger + ": command not in table");
                    return;
                }

                int position = bar.Controls.Count + 1;
                cmd.AddControl(bar, position);
                jsonMenuEnsured = true;
                BlameMenuLog(trigger + ": added at " + position + ", count now " + bar.Controls.Count);
            }
            catch (Exception ex)
            {
                // JSON editor package not ready / menu store locked / VS shutting down - retried on next trigger
                BlameMenuLog(trigger + ": EX " + ex.GetType().Name + " " + ex.Message);
            }
        }

        #endregion

        #region Toggle P4 Blame command

        private void OnToggleBlameBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            EnsureJsonContextMenuBlameButton("querystatus");

            OleMenuCommand cmd = (OleMenuCommand)sender;
            cmd.Checked = false;
            cmd.Enabled = cmd.Visible = false;

            IWpfTextView view = GetActiveWpfTextView();
            if (view == null || GetFilePath(view) == null)
                return;

            cmd.Enabled = cmd.Visible = true;

            Blame.BlameMargin margin;
            if (view.Properties.TryGetProperty(typeof(Blame.BlameMargin), out margin) && margin != null)
            {
                cmd.Checked = margin.IsBlameVisible;
            }
        }

        private void OnToggleBlameExecute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            IWpfTextView view = GetActiveWpfTextView();
            if (view == null)
                return;

            Blame.BlameMargin margin;
            if (view.Properties.TryGetProperty(typeof(Blame.BlameMargin), out margin) && margin != null)
            {
                margin.Toggle();
            }
            else
            {
                VsShellUtilities.ShowMessageBox(this,
                    "Blame margin is not available for this editor. Close and re-open the file, then retry.",
                    "P4 Blame",
                    OLEMSGICON.OLEMSGICON_INFO,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }

        private IWpfTextView GetActiveWpfTextView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            IVsTextManager textManager = GetService(typeof(SVsTextManager)) as IVsTextManager;
            if (textManager == null)
                return null;

            IVsTextView vsTextView;
            if (textManager.GetActiveView(1, null, out vsTextView) != VSConstants.S_OK || vsTextView == null)
                return null;

            IComponentModel componentModel = GetService(typeof(SComponentModel)) as IComponentModel;
            IVsEditorAdaptersFactoryService adapters = componentModel?.GetService<IVsEditorAdaptersFactoryService>();
            return adapters?.GetWpfTextView(vsTextView);
        }

        private static string GetFilePath(IWpfTextView view)
        {
            Microsoft.VisualStudio.Text.ITextDocument doc;
            if (view.TextBuffer.Properties.TryGetProperty(typeof(Microsoft.VisualStudio.Text.ITextDocument), out doc)
                && doc != null
                && !string.IsNullOrEmpty(doc.FilePath))
            {
                return doc.FilePath;
            }

            return null;
        }

        #endregion

        #region IOleCommandTarget

        int IOleCommandTarget.QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            //System.Windows.Forms.MessageBox.Show("IOleCommandTarget.QueryStatus");

            if (pguidCmdGroup == GuidAndCmdID.guidCmdSet)
            {
                switch (prgCmds[0].cmdID)
                {
                    case GuidAndCmdID.cmdidNavigateToChangelist:
                        prgCmds[0].cmdf |= (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED | OLECMDF.OLECMDF_INVISIBLE);
                        return VSConstants.S_OK;
                    case GuidAndCmdID.cmdidShowHistory:
                        prgCmds[0].cmdf |= (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED | OLECMDF.OLECMDF_INVISIBLE);
                        return VSConstants.S_OK;
                }
            }

            return this.pkgCommandTarget.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }

        int IOleCommandTarget.Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            //System.Windows.Forms.MessageBox.Show("IOleCommandTarget.Exec");

            if (pguidCmdGroup == GuidAndCmdID.guidCmdSet)
            {
                switch (nCmdID)
                {
                    case GuidAndCmdID.cmdidNavigateToChangelist:
                        if (IsQueryParameterList(pvaIn, pvaOut, nCmdexecopt))
                        {
                            Marshal.GetNativeVariantForObject("p", pvaOut);
                            return VSConstants.S_OK;
                        }
                        else
                        {
                            // no args
                            if (pvaIn == IntPtr.Zero)
                                return VSConstants.S_FALSE;

                            object vaInObject = Marshal.GetObjectForNativeVariant(pvaIn);
                            if (vaInObject == null || vaInObject.GetType() != typeof(string))
                                return VSConstants.E_INVALIDARG;

                            if ((vaInObject is string commitId) && !string.IsNullOrEmpty(commitId))
                            {
                                NavigateToCommit(commitId, this as IServiceProvider);
                            }
                        }
                        return VSConstants.S_OK;

                    case GuidAndCmdID.cmdidShowHistory:
                        if (IsQueryParameterList(pvaIn, pvaOut, nCmdexecopt))
                        {
                            Marshal.GetNativeVariantForObject("p", pvaOut);
                            return VSConstants.S_OK;
                        }
                        else
                        {
                            ShowHistory(this as IServiceProvider);
                        }
                        return VSConstants.S_OK;

                    case GuidAndCmdID.cmdidTimeLapseView:
                        if (IsQueryParameterList(pvaIn, pvaOut, nCmdexecopt))
                        {
                            Marshal.GetNativeVariantForObject("p", pvaOut);
                            return VSConstants.S_OK;
                        }
                        else
                        {
                            // no args
                            if (pvaIn == IntPtr.Zero)
                                return VSConstants.S_FALSE;

                            object vaInObject = Marshal.GetObjectForNativeVariant(pvaIn);
                            if (vaInObject == null || vaInObject.GetType() != typeof(string))
                                return VSConstants.E_INVALIDARG;

                            if ((vaInObject is string filePath) && !string.IsNullOrEmpty(filePath))
                            {
                                TimeLapseView(filePath, this as IServiceProvider);
                            }
                        }
                        return VSConstants.S_OK;
                }
            }

            return this.pkgCommandTarget.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        #endregion

        private static void NavigateToCommit(string commitId, IServiceProvider serviceProvider)
        {
            bool test = P4VsProvider.CurrentScm.CheckConnection();
            P4ScmProvider scm = P4VsProvider.CurrentScm;

            SubmittedChangelistDlg submitted = new SubmittedChangelistDlg(scm, true);
            int.TryParse(commitId, out int ID);
            submitted.ChangeListId = ID;

            Perforce.P4.ServerMetaData smd = scm.GetServerMetaData();
            submitted.Text = string.Format(Resources.SubmittedChangelistsToolWindowControl_SubmittedChangelistDlgCaption,
                                     commitId, smd.Address.Uri, scm.Connection.Repository.Connection.UserName);

            submitted.ShowDialog();
        }

        private static void ShowHistory(IServiceProvider serviceProvider)
        {
            P4ScmProvider scm = P4VsProvider.CurrentScm;
            if (scm != null)
            {
                scm.LaunchHistoryWindow();
            }
        }

        private static void TimeLapseView(string filePath, IServiceProvider serviceProvider)
        {
            P4ScmProvider scm = P4VsProvider.CurrentScm;
            if (scm != null)
            {
                scm.LaunchTimeLapseView(filePath);
            }
        }

        private static bool IsQueryParameterList(IntPtr pvaIn, IntPtr pvaOut, uint nCmdexecopt)
        {
            ushort lo = (ushort)(nCmdexecopt & (uint)0xffff);
            ushort hi = (ushort)(nCmdexecopt >> 16);
            if (lo == (ushort)OLECMDEXECOPT.OLECMDEXECOPT_SHOWHELP)
            {
                if (hi == VsMenus.VSCmdOptQueryParameterList)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
