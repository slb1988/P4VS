using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;

namespace CodeLensOopProviderVsix.Blame
{
    /// <summary>
    /// MEF provider that attaches a (collapsed) <see cref="BlameMargin"/> to every
    /// document text view. The margin is toggled on/off per view by the
    /// "Toggle P4 Blame" editor context-menu command, which finds the margin
    /// through the view property bag.
    /// </summary>
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(BlameMargin.MarginName)]
    [Order(After = PredefinedMarginNames.LineNumber)]
    [MarginContainer(PredefinedMarginNames.Left)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class BlameMarginProvider : IWpfTextViewMarginProvider
    {
        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        {
            BlameMargin margin = new BlameMargin(wpfTextViewHost.TextView);
            wpfTextViewHost.TextView.Properties[typeof(BlameMargin)] = margin;
            return margin;
        }
    }
}
