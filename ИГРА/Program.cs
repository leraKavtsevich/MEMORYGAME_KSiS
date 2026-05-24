using MemoryGame.Services;
using MemoryGame.Handlers;  

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddSingleton<GameRoomManager>();
builder.Services.AddSingleton<RawWebSocketHandler>(); 

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseStaticFiles();
app.UseRouting();

var wsHandler = app.Services.GetRequiredService<RawWebSocketHandler>();
_ = Task.Run(() => wsHandler.StartServer(8081));

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();