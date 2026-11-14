using System.Globalization;
using Capture.Core.Redaction;
using Capture.Windows;

namespace Capture.App.Commands;

/// <summary>--redaction-test [scale] [--verbose]: checks secret redaction on this machine with made-up secrets.</summary>
internal static class RedactionTestCommand
{
    public static int Run(string[] args)
    {
        double scale = args.Length > 1 && double.TryParse(args[1], CultureInfo.InvariantCulture, out double s)
            ? s
            : WindowsOcrRecognizer.DefaultScale;
        bool verbose = args.Contains("--verbose");

        WindowsOcrRecognizer? recognizer = WindowsOcrRecognizer.TryCreate(scale);
        if (recognizer is null)
        {
            return AppConsole.Fail(4, "Windows has no OCR language installed.");
        }

        RedactionSelfTest.Report report = Task
            .Run(() => RedactionSelfTest.RunAsync(recognizer, [new HeuristicSecretDetector()]))
            .GetAwaiter().GetResult();

        foreach (var group in report.Cases.GroupBy(c => (c.Theme, c.FontSize)))
        {
            string bySide = string.Join(", ", group.GroupBy(c => c.Side)
                .Select(side => $"{side.Key} side {side.Count(c => c.Covered)}/{side.Count(c => c.Found)}"));
            Console.WriteLine($"{group.Key.Theme} theme, {group.Key.FontSize} pt: {group.Count(c => c.Found)} of {group.Count()} fake secrets found; box covers the secret: {bySide}");
            foreach (RedactionSelfTest.Case problem in group.Where(c => !c.Covered && verbose))
            {
                Console.WriteLine($"  {(problem.Found ? "MISPLACED" : "MISSED   ")} ({problem.Side}) {problem.Line}");
                Console.WriteLine($"  read as   {problem.RecognizedAs}");
            }
        }
        Console.WriteLine($"False positives on harmless lines: {report.FalsePositives}");
        Console.WriteLine($"Text recognition and detection: {report.MillisecondsPerImage} ms per {report.Width}x{report.Height} image ({recognizer.LanguageTag}, enlarged {scale}x)");
        return report.Cases.All(c => c.Covered) ? 0 : 5;
    }
}
