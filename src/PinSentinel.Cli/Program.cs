using System.Globalization;
using PinSentinel.Core;

return args.FirstOrDefault() switch
{
    "probe" => Probe(),
    "watch" => Watch(args[1..]),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("""
        pinsentinel probe
            List GPUs and dump raw and parsed sensor frames.

        pinsentinel watch [--interval <ms>] [--log <dir>] [--seconds <n>]
            Sample continuously and report rule findings. Warnings only, takes no action.
            --log writes one CSV per day into the directory.
        """);
    return 1;
}

static AstralSensor? OpenSensor()
{
    var sensor = AstralSensor.Open();
    if (sensor is null) Console.Error.WriteLine("No supported ROG Astral card found.");
    return sensor;
}

static int Probe()
{
    foreach (var gpu in AstralSensor.ListGpus())
    {
        string name = AstralSensor.SupportedCards.GetValueOrDefault(gpu.SubSystemId, "unsupported");
        Console.WriteLine($"GPU {gpu.Index}: device {gpu.DeviceId:X8} subsystem {gpu.SubSystemText} ({name})");
    }

    var sensor = OpenSensor();
    if (sensor is null) return 2;

    Span<byte> raw = stackalloc byte[PinFrame.RawLength];
    for (int i = 0; i < 3; i++)
    {
        if (!sensor.TryReadRaw(raw))
        {
            Console.Error.WriteLine($"I2C read failed, NVAPI status {sensor.LastStatus}");
            return 3;
        }
        Console.WriteLine($"raw: {Convert.ToHexString(raw)}");
        if (!PinFrame.TryParse(raw, DateTimeOffset.Now, out var frame))
        {
            Console.Error.WriteLine("Frame failed plausibility checks.");
            return 4;
        }
        for (int p = 0; p < frame!.Pins.Count; p++)
            Console.WriteLine($"  pin {p + 1}: {frame.Pins[p].Volts,7:F3} V {frame.Pins[p].Amps,7:F3} A");
        Console.WriteLine($"  total {frame.TotalAmps:F2} A, {frame.Watts:F1} W, spread {frame.VoltSpread * 1000:F0} mV");
        Thread.Sleep(500);
    }
    return 0;
}

static int Watch(string[] args)
{
    int intervalMs = 500;
    double seconds = double.PositiveInfinity;
    string? logDir = null;
    for (int i = 0; i + 1 < args.Length; i += 2)
    {
        switch (args[i])
        {
            case "--interval": intervalMs = int.Parse(args[i + 1]); break;
            case "--seconds": seconds = double.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
            case "--log": logDir = args[i + 1]; break;
            default: return Usage();
        }
    }

    var sensor = OpenSensor();
    if (sensor is null) return 2;

    using var log = logDir is null ? null : new CsvLog(logDir);
    var engine = new RuleEngine(new RuleOptions());
    var active = new HashSet<string>();
    var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    var started = DateTimeOffset.Now;

    while (!stop.IsCancellationRequested && (DateTimeOffset.Now - started).TotalSeconds < seconds)
    {
        var frame = sensor.Read();
        var eval = frame is null ? engine.EvaluateReadFailure() : engine.Evaluate(frame);

        if (frame is null)
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.f}  read failed (status {sensor.LastStatus})");
        else
        {
            log?.Write(frame, eval);
            string amps = string.Join(' ', frame.Pins.Select(p => p.Amps.ToString("F2", CultureInfo.InvariantCulture).PadLeft(5)));
            Console.WriteLine($"{frame.Timestamp:HH:mm:ss.f}  {amps} A  {frame.Watts,5:F0} W  {frame.VoltSpread * 1000,3:F0} mV  {eval.Severity}");
        }

        foreach (var f in eval.Findings)
            if (active.Add(f.RuleId)) Console.WriteLine($"  [{f.Severity}] {f.RuleId}: {f.Message}");
        active.IntersectWith(eval.Findings.Select(f => f.RuleId));

        stop.Token.WaitHandle.WaitOne(intervalMs);
    }
    return 0;
}
