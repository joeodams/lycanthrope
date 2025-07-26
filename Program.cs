using lycanthrope.Components;
using lycanthrope.Interfaces;
using lycanthrope.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Add SignalR
builder.Services.AddSignalR();

// Add the lobby management service
builder.Services.AddScoped<IGameEngineService, GameEngineService>();

var multiplexer = ConnectionMultiplexer.Connect("localhost:6379,allowAdmin=true");
var server = multiplexer.GetServer("localhost:6379");
server.FlushAllDatabases();
builder.Services.AddScoped<IDatabase>(cfg =>
{
    return multiplexer.GetDatabase();
});
builder.Services.AddScoped<ISubscriber>(cfg =>
{
    return multiplexer.GetSubscriber();
});
var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapHub<LobbyHub>("/lobbyhub");

app.Run();
