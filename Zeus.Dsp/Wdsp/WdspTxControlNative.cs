// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Dsp.Wdsp;

internal interface IWdspTxControlNative
{
    void SetTXAMode(int channel, int mode);
    void SetTXACFCOMPRun(int channel, int run);
    void SetTXACFCOMPprofile(int channel, int nfreqs, double[] f, double[] g, double[] e);
    void SetTXACFCOMPPrecomp(int channel, double precomp);
    void SetTXACFCOMPPrePeq(int channel, double prepeq);
    void SetTXACFCOMPPeqRun(int channel, int run);
    void SetTXAPHROTRun(int channel, int run);
    void SetTXAPHROTCorner(int channel, double frequency);
    void SetTXAPHROTNstages(int channel, int nstages);
    void SetTXAPHROTAutoMode(int channel, int autoMode);
    void SetTXAPHROTReverse(int channel, int reverse);
}

internal sealed class WdspTxControlNative : IWdspTxControlNative
{
    public void SetTXAMode(int channel, int mode) =>
        NativeMethods.SetTXAMode(channel, mode);

    public void SetTXACFCOMPRun(int channel, int run) =>
        NativeMethods.SetTXACFCOMPRun(channel, run);

    public unsafe void SetTXACFCOMPprofile(int channel, int nfreqs, double[] f, double[] g, double[] e)
    {
        fixed (double* pF = f, pG = g, pE = e)
        {
            NativeMethods.SetTXACFCOMPprofile(
                channel, nfreqs,
                ref *pF, ref *pG, ref *pE);
        }
    }

    public void SetTXACFCOMPPrecomp(int channel, double precomp) =>
        NativeMethods.SetTXACFCOMPPrecomp(channel, precomp);

    public void SetTXACFCOMPPrePeq(int channel, double prepeq) =>
        NativeMethods.SetTXACFCOMPPrePeq(channel, prepeq);

    public void SetTXACFCOMPPeqRun(int channel, int run) =>
        NativeMethods.SetTXACFCOMPPeqRun(channel, run);

    public void SetTXAPHROTRun(int channel, int run) =>
        NativeMethods.SetTXAPHROTRun(channel, run);

    public void SetTXAPHROTCorner(int channel, double frequency) =>
        NativeMethods.SetTXAPHROTCorner(channel, frequency);

    public void SetTXAPHROTNstages(int channel, int nstages) =>
        NativeMethods.SetTXAPHROTNstages(channel, nstages);

    public void SetTXAPHROTAutoMode(int channel, int autoMode) =>
        NativeMethods.SetTXAPHROTAutoMode(channel, autoMode);

    public void SetTXAPHROTReverse(int channel, int reverse) =>
        NativeMethods.SetTXAPHROTReverse(channel, reverse);
}
