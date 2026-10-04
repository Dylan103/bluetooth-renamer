using System;
using System.Windows;
using BluetoothRenamer;

internal static class DpiTests
{
    private static int checks;

    public static int Main()
    {
        try
        {
            AssertRect("valid bounds stay unchanged",
                new Rect(140, 31, 1000, 690),
                DpiSupport.ClampBounds(new Rect(140, 31, 1000, 690), new Rect(0, 0, 1280, 752)));
            AssertRect("removed monitor recovers to remaining work area",
                new Rect(1020, 100, 900, 600),
                DpiSupport.ClampBounds(new Rect(2500, 100, 900, 600), new Rect(0, 0, 1920, 1040)));
            AssertRect("negative monitor origin is preserved",
                new Rect(-1920, -100, 900, 600),
                DpiSupport.ClampBounds(new Rect(-2300, -150, 900, 600), new Rect(-1920, -100, 1920, 1040)));
            AssertRect("oversized window shrinks to work area",
                new Rect(0, 0, 1920, 1040),
                DpiSupport.ClampBounds(new Rect(-100, -100, 2500, 1800), new Rect(0, 0, 1920, 1040)));
            AssertRect("small work area remains reachable",
                new Rect(50, 40, 480, 320),
                DpiSupport.ClampBounds(new Rect(100, 100, 1000, 690), new Rect(50, 40, 480, 320)));
            AssertRejected("empty candidate", delegate { DpiSupport.ClampBounds(Rect.Empty, new Rect(0, 0, 100, 100)); });
            AssertRejected("zero work area", delegate { DpiSupport.ClampBounds(new Rect(0, 0, 100, 100), new Rect(0, 0, 0, 100)); });
            AssertRejected("NaN origin", delegate { DpiSupport.ClampBounds(new Rect(Double.NaN, 0, 100, 100), new Rect(0, 0, 100, 100)); });
            AssertRejected("infinite width", delegate { DpiSupport.ClampBounds(new Rect(0, 0, Double.PositiveInfinity, 100), new Rect(0, 0, 100, 100)); });
            Console.WriteLine("PASS: " + checks + " DPI geometry checks. No windows or display settings were changed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error.Message);
            return 1;
        }
    }

    private static void AssertRect(string name, Rect expected, Rect actual)
    {
        if (actual != expected)
            throw new InvalidOperationException(name + ": expected " + expected + ", got " + actual);
        checks++;
    }

    private static void AssertRejected(string name, Action action)
    {
        try { action(); }
        catch (ArgumentException) { checks++; return; }
        throw new InvalidOperationException(name + ": invalid geometry was accepted");
    }
}
