# Mini File Manager

`MiniFileManager/FileManagerService.cs` - file manager service. It adds minimal API endpoints that perform file management operations. At the specified path, it loads html with a javascript script that is responsible for the logic on the client side.

With this simple service structure, you can easily add new functionality and change the front-end part for your needs.

## Demo

![](https://github.com/komarowski/MiniFileManager/blob/main/images/demo.gif)

## Usage

We need to register the service in the DI container and call its endpoint registration method from `Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);
// Specify the file manager root directory and index URL page.
builder.Services.AddScoped(provider => new FileManagerService("wwwroot", new PathString("/filemanager")));
var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
  var service = scope.ServiceProvider.GetService<FileManagerService>();
  service.RegisterFileManagerEndpoints(app);
}

app.Run();
```

You can also specify your own template for the html page of the file manager. An example - `MiniFileManager/filemanager.html`.

```csharp
builder.Services.AddScoped(provider => new FileManagerService("wwwroot", new PathString("/filemanager"), "filemanager.html"));
```