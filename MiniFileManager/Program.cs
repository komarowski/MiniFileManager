using MiniFileManager;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseMiddleware<FileManagerMiddleware>("wwwroot", new PathString("/filemanager"), "filemanager.html");

app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();
