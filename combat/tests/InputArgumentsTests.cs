using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using BannerlordCombatBridge;

public static class InputArgumentsTests
{
    private static int _checks;
    private static string _gameRoot;

    public static int Main(string[] args)
    {
        _gameRoot = args.Length == 0
            ? @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord"
            : args[0];
        AppDomain.CurrentDomain.AssemblyResolve += ResolveGameAssembly;
        try
        {
            Run();
            Console.WriteLine("Combat input arguments: " + _checks + " checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Combat input arguments failed after " + _checks + " checks: " + ex);
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        Accept("0|0|0|0|none|none|0");
        Accept("1|-1|3.1415927|1.4|none|none|1");
        Accept("-1|1|-3.1415927|-1.4|none|none|0");
        Accept(".5|-0.25|1e0|-1e-1|none|none|0");
        Accept(" 0 |0|0|0|none|none|0");
        string[] directions = { "up", "down", "left", "right" };
        foreach (string direction in directions)
        {
            Accept("0|0|0|0|" + direction + "|none|0");
            Accept("0|0|0|0|none|" + direction + "|0");
            foreach (string opposite in directions)
                Reject("0|0|0|0|" + direction + "|" + opposite + "|0");
        }
        Reject(null);
        Reject("");
        Reject("0|0|0|0|none|none");
        Reject("0|0|0|0|none|none|0|extra");
        Reject("0|0|0|0|none|none|0|");
        Reject("1.0001|0|0|0|none|none|0");
        Reject("-1.0001|0|0|0|none|none|0");
        Reject("0|1.0001|0|0|none|none|0");
        Reject("0|-1.0001|0|0|none|none|0");
        Reject("0|0|3.142|0|none|none|0");
        Reject("0|0|-3.142|0|none|none|0");
        Reject("0|0|0|1.401|none|none|0");
        Reject("0|0|0|-1.401|none|none|0");
        foreach (string value in new[] { "NaN", "Infinity", "-Infinity", "1e1000", "", "no", "1,5" })
        {
            for (int field = 0; field < 4; field++)
            {
                string[] fields = { "0", "0", "0", "0", "none", "none", "0" };
                fields[field] = value;
                Reject(string.Join("|", fields));
            }
        }
        foreach (string direction in new[] { "UP", "None", "auto", "kick", "", " up" })
        {
            Reject("0|0|0|0|" + direction + "|none|0");
            Reject("0|0|0|0|none|" + direction + "|0");
        }
        foreach (string jump in new[] { "2", "-1", "true", "false", "0.0", "", " 1" })
            Reject("0|0|0|0|none|none|" + jump);

        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Accept("0.5|-0.5|1.5|-0.4|none|none|0");
            Reject("0,5|0|0|0|none|none|0");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void Accept(string value)
    {
        string reason = PlayerInput.ValidateArguments(value);
        _checks++;
        if (reason != null) throw new Exception("Expected acceptance: " + value + "; " + reason);
    }

    private static void Reject(string value)
    {
        string reason = PlayerInput.ValidateArguments(value);
        _checks++;
        if (string.IsNullOrEmpty(reason)) throw new Exception("Expected rejection: " + value);
    }

    private static Assembly ResolveGameAssembly(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        string[] directories = {
            Path.Combine(_gameRoot, @"bin\Win64_Shipping_Client"),
            Path.Combine(_gameRoot, @"Modules\Native\bin\Win64_Shipping_Client"),
            Path.Combine(_gameRoot, @"Modules\SandBoxCore\bin\Win64_Shipping_Client")
        };
        foreach (string directory in directories)
        {
            string file = Path.Combine(directory, name);
            if (File.Exists(file)) return Assembly.LoadFrom(file);
        }
        return null;
    }
}
