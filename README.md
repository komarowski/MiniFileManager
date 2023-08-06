# Mini File Manager Middleware

`MiniFileManager/FileManagerMiddleware.cs` - a custom middleware that acts like a file manager. At the specified path, it loads html with a javascript script that sends requests to perform file management operations. These requests are also caught and executed on this middleware.

Thanks to this structure, you can easily add new functionality and change the front-end part for your needs.

## Demo


## Usage

The following code calls the middleware from `Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Specify the file manager root directory and index URL page.
app.UseMiddleware<FileManagerMiddleware>("wwwroot", new PathString("/filemanager"));

app.Run();
```

You can also specify your own template for the html page of the file manager. An example in the file `MiniFileManager/filemanager.html`.

```csharp
app.UseMiddleware<FileManagerMiddleware>("wwwroot", new PathString("/filemanager"), "filemanager.html");
```