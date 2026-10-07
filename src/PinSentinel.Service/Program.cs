using PinSentinel.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "PinSentinel");
builder.Services.Configure<ServiceOptions>(builder.Configuration.GetSection("PinSentinel"));
builder.Services.AddSingleton<WindowsGuardActions>();
builder.Services.AddSingleton<StatusBroadcaster>();
builder.Services.AddHostedService<GuardWorker>();

builder.Build().Run();
