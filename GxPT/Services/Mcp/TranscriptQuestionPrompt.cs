using System;
using System.Threading;
using System.Windows.Forms;

namespace GxPT
{
    // Bridges the synchronous IQuestionPrompt (called on the tool-loop worker thread) to the docked
    // QuestionPanel on the UI thread. Ask blocks the worker until the user submits or skips; the turn
    // pauses there, which is correct - the user is present. A near-copy of TranscriptApprovalPrompt.
    internal sealed class TranscriptQuestionPrompt : IQuestionPrompt
    {
        private readonly Control _uiMarshal;          // any control on the UI thread (the form)
        private readonly Func<QuestionPanel> _getPanel;

        public TranscriptQuestionPrompt(Control uiMarshal, Func<QuestionPanel> getPanel)
        {
            _uiMarshal = uiMarshal;
            _getPanel = getPanel;
        }

        public QuestionAnswer Ask(QuestionRequest request)
        {
            if (_uiMarshal == null || _getPanel == null || request == null)
                return QuestionAnswer.DismissedAnswer();

            QuestionAnswer[] result = { null };
            using (ManualResetEvent done = new ManualResetEvent(false))
            {
                try
                {
                    _uiMarshal.BeginInvoke((MethodInvoker)delegate
                    {
                        // Any UI failure (panel resolver, a disposed panel mid-show) must still signal the
                        // waiting worker - an unset event strands the turn forever.
                        try
                        {
                            QuestionPanel panel = _getPanel();
                            if (panel == null) { done.Set(); return; }
                            panel.ShowQuestion(request, delegate(QuestionAnswer answer)
                            {
                                result[0] = answer;
                                done.Set();
                            });
                        }
                        catch { done.Set(); }
                    });
                }
                catch
                {
                    return QuestionAnswer.DismissedAnswer(); // UI gone (e.g. closing) -> safe default
                }

                done.WaitOne();
            }
            return result[0] != null ? result[0] : QuestionAnswer.DismissedAnswer();
        }
    }
}
