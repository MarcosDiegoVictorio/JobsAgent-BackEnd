using JobAgent.Service.Extensions;
using JobAgent.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Register Core domain, repository & services
builder.Services.AddJobAgentCore(builder.Configuration);

// Register Worker BackgroundService
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
