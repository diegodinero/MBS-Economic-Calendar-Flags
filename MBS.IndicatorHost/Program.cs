// This project exists solely to satisfy Visual Studio's requirement that a solution
// have an executable startup project in order to enable debugging.
//
// The MBS Economic Calendar Flags indicator runs inside the Quantower trading platform
// and cannot be launched standalone. To debug the indicator:
//   1. Build the solution (Ctrl+Shift+B) — the DLL is copied to Quantower's Indicators folder.
//   2. Start Quantower normally.
//   3. In Visual Studio, go to Debug → Attach to Process and attach to Quantower's process.
//   4. Breakpoints set in MBS_Economic_Calendar_Flags.cs will be hit at runtime.

Console.WriteLine("MBS Economic Calendar Flags — Debug Host");
Console.WriteLine();
Console.WriteLine("This host project exists only to enable Visual Studio debugging.");
Console.WriteLine("The indicator DLL must be loaded inside the Quantower platform.");
Console.WriteLine();
Console.WriteLine("Steps to debug:");
Console.WriteLine("  1. Build the solution to deploy the DLL to Quantower's Indicators folder.");
Console.WriteLine("  2. Launch Quantower.");
Console.WriteLine("  3. In Visual Studio: Debug > Attach to Process > select Quantower.");
Console.WriteLine("  4. Set breakpoints in MBS_Economic_Calendar_Flags.cs.");
