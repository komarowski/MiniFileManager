using MiniFileManager;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped(provider => new FileManagerService("wwwroot", new PathString("/filemanager"), "filemanager.html"));
var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
  var service = scope.ServiceProvider.GetService<FileManagerService>();
  service.RegisterFileManagerEndpoints(app);
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();
