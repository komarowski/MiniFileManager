using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MiniFileManager
{
  /// <summary>
  /// File manager middleware.
  /// </summary>
  public class FileManagerMiddleware
  {
    private readonly RequestDelegate next;
    private readonly PhysicalFileProvider fileProvider;
    private readonly PathString url;
    private readonly int urlLength;
    private readonly string root;
    private readonly string? htmlTemplate;
    private string? html;

    /// <summary>
    /// Public constructor for file manager middleware.
    /// </summary>
    /// <param name="next">A function that can process an HTTP request.</param>
    /// <param name="root">File manager root directory.</param>
    /// <param name="url">File manager index URL page.</param>
    /// <param name="htmlTemplate">File manager html template file name.</param>
    /// <exception cref="DirectoryNotFoundException"></exception>
    public FileManagerMiddleware(RequestDelegate next, string root, PathString url, string? htmlTemplate = null)
    {
      if (!Directory.Exists(root))
      {
        throw new DirectoryNotFoundException($"\"{root}\" directory not exist!");
      }
      this.fileProvider = new PhysicalFileProvider(Path.Combine(Directory.GetCurrentDirectory(), root));
      this.url = url;
      this.urlLength = url.ToString().Length;
      this.root = root;
      this.htmlTemplate = htmlTemplate;
      this.next = next;
    }

    public async Task Invoke(HttpContext context)
    {
      if (context.Request.Path.StartsWithSegments(this.url))
      {
        var relativePath = context.Request.Path.ToString()[this.urlLength..];
        try
        {
          var isRequestCompleted = await HandleRequestAsync(context, relativePath);
          if (isRequestCompleted)
          {
            return;
          }
        }
        catch (Exception ex)
        {
          await HandleExceptionAsync(context, ex);
          return;
        }
      }
      await this.next.Invoke(context);
    }

    /// <summary>
    /// Handles a request based on its method and path.
    /// </summary>
    /// <param name="context">Information about request.</param>
    /// <param name="path">Request path.</param>
    /// <returns>true if the request was processed successfully.</returns>
    async Task<bool> HandleRequestAsync(HttpContext context, string path) => (path, context.Request.Method) switch
    {
      ("/", "GET") or ("", "GET") => await SetResponseAsync(context, GetFileManagerHtml(context), "text/html"),
      ("/files", "GET") => await SetResponseAsync(context, GetDirectoryContents(context), "application/json"),
      ("/file", "GET") => await SetResponseAsync(context, await GetFileContentAsync(context), "text/plain"),
      ("/file", "POST") => await SaveFileAsync(context),
      ("/file", "DELETE") => DeleteFile(context),
      ("/folder", "POST") => AddFolder(context),
      ("/folder", "DELETE") => DeleteDirectory(context),
      ("/upload", "POST") => await UploadFilesAsync(context),
      ("/download", "GET") => await DownloadAsync(context),
      ("/view", "GET") => await SendFileAsync(context),
      _ => false
    };

    /// <summary>
    /// Handles an exception that may occur in <see cref="HandleRequestAsync"/>.
    /// </summary>
    /// <param name="context">Information about request.</param>
    /// <param name="exception">Exception.</param>
    /// <returns>Information about the exception as a json string.</returns>
    private static async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
      context.Response.ContentType = "application/json";
      context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
      await context.Response.WriteAsync($"{{ message: \"{exception.Message}\", innerException: \"{exception.InnerException?.Message}\"}}");
    }

    /// <summary>
    /// Sets the response body.
    /// </summary>
    /// <param name="context">Information about request.</param>
    /// <param name="body">Response body.</param>
    /// <param name="type">Response content type.</param>
    /// <returns>true if the response body is set.</returns>
    private static async Task<bool> SetResponseAsync(HttpContext context, string? body, string type)
    {
      if (body is null)
      {
        return false;
      }

      byte[] bytes = Encoding.UTF8.GetBytes(body);
      context.Response.StatusCode = (int)HttpStatusCode.OK;
      context.Response.ContentType = type;
      context.Response.ContentLength = bytes.Length;
      await context.Response.Body.WriteAsync(bytes);
      return true;
    }

    /// <summary>
    /// Gets the html of the main page of the file manager.
    /// </summary>
    /// <param name="context">Information about request.</param>
    /// <returns>html text.</returns>
    private string GetFileManagerHtml(HttpContext context)
    {
      if (this.html is null)
      {
        if (!string.IsNullOrEmpty(this.htmlTemplate) && File.Exists(this.htmlTemplate))
        {
          this.html = File.ReadAllText(this.htmlTemplate);
        }
        else
        {
          this.html = HtmlTemplate;
        }
        // In html replace "{@apiUrl}" with the full URL of the file manager.
        // This is for fetch requests in js code.
        var apiUrl = $"{context.Request.Scheme}://{context.Request.Host.Value}{this.url}";
        this.html = this.html.Replace("{@apiUrl}", apiUrl);
      }
      return this.html;
    }

    /// <summary>
    /// Gets the "path" value from request query.
    /// </summary>
    /// <param name="context">Information about request.</param>
    /// <param name="path">"path" value.</param>
    /// <returns>true if query contains the "path" value.</returns>
    private static bool TryGetPathValue(HttpContext context, out StringValues path)
    {
      return context.Request.Query.TryGetValue("path", out path);
    }

    /// <summary>
    /// Gets the full path from "path" value from request query.
    /// </summary>
    /// <param name="context">Information about request.</param>
    /// <param name="fullPath">The full path of the file or directory.</param>
    /// <param name="isDirectory">true if the full path is a directory.</param>
    /// <returns>true if the full path exists as a file or directory.</returns>
    private bool TryGetFullPath(HttpContext context, out string fullPath, bool isDirectory = false)
    {
      fullPath = string.Empty;
      if (TryGetPathValue(context, out var relativePath))
      {
        fullPath = this.root + relativePath;
        return isDirectory
          ? Directory.Exists(fullPath)
          : File.Exists(fullPath);
      }
      return false;
    }

    /// <summary>
    /// Gets the contents of a directory.
    /// </summary>
    /// <param name="context">Information about request.</param>
    /// <returns><see cref="IDirectoryContents"/> json string or null.</returns>
    private string? GetDirectoryContents(HttpContext context)
    {
      if (TryGetPathValue(context, out var path))
      {
        var directoryContent = this.fileProvider.GetDirectoryContents(path);
        return JsonSerializer.Serialize(directoryContent);
      }
      return null;
    }

    /// <summary>
    /// Gets the contents of a file.
    /// </summary>
    /// <param name="context">Information about request.</param>
    /// <returns>File text or null.</returns>
    private async Task<string?> GetFileContentAsync(HttpContext context)
    {
      if (TryGetFullPath(context, out var fullPath))
      {
        return await File.ReadAllTextAsync(fullPath);
      }
      return null;
    }

    /// <summary>
    /// Saves the file.
    /// </summary>
    /// <param name="context">Information about request.</param>
    /// <returns>true if file is saved.</returns>
    private async Task<bool> SaveFileAsync(HttpContext context)
    {
      if (TryGetFullPath(context, out var directory, true))
      {
        var name = context.Request.Form["Name"];
        var text = context.Request.Form["Text"];
        var fullPath = Path.Combine(directory, name);
        await File.WriteAllTextAsync(fullPath, text, Encoding.UTF8);
        return true;
      }
      return false;
    }

    /// <summary>
    /// Deletes a file.
    /// </summary>
    /// <param name="context">Information about request.</param>
    /// <returns>true if the file is deleted.</returns>
    private bool DeleteFile(HttpContext context)
    {
      if (TryGetFullPath(context, out var path))
      {
        File.Delete(path);
        return true;
      }
      return false;
    }

    /// <summary>
    /// Adds a folder.
    /// </summary>
    /// <param name="context">Information about request.</param>
    /// <returns>true if the folder is added.</returns>
    private bool AddFolder(HttpContext context)
    {
      if (TryGetFullPath(context, out var directory, true))
      {
        var name = context.Request.Form["Name"];
        var fullPath = Path.Combine(directory, name);
        if (!Directory.Exists(fullPath))
        {
          Directory.CreateDirectory(fullPath);
          return true;
        }
      }
      return false;
    }

    /// <summary>
    /// Deletes a directory.
    /// </summary>
    /// <param name="context">Information about request.</param>
    /// <returns>true if the directory is deleted.</returns>
    private bool DeleteDirectory(HttpContext context)
    {
      if (TryGetFullPath(context, out var directory, true))
      {
        Directory.Delete(directory);
        return true;
      }
      return false;
    }

    /// <summary>
    /// Uploads files to the current directory.
    /// </summary>
    /// <param name="context">Information about request.</param>
    /// <returns>true if files have been uploaded.</returns>
    private async Task<bool> UploadFilesAsync(HttpContext context)
    {
      if (TryGetFullPath(context, out var directory, true))
      {
        foreach (var file in context.Request.Form.Files)
        {
          var fullPath = Path.Combine(directory, file.FileName);
          using Stream fileStream = new FileStream(fullPath, FileMode.Create);
          await file.CopyToAsync(fileStream);
        }
        return true;
      }
      return false;
    }

    /// <summary>
    /// Downloads the current directory as a zip file.
    /// </summary>
    /// <param name="context">Information about request.</param>
    /// <returns>true if the file has been downloaded.</returns>
    private async Task<bool> DownloadAsync(HttpContext context)
    {
      if (TryGetFullPath(context, out var directory, true))
      {
        var zipFileName = "backup.zip";
        if (File.Exists(zipFileName))
        {
          File.Delete(zipFileName);
        }
        ZipFile.CreateFromDirectory(directory, zipFileName);
        context.Response.ContentType = "application/zip";
        context.Response.Headers.ContentDisposition = $"attachment; filename={zipFileName}";
        await context.Response.SendFileAsync(zipFileName);
        return true;
      }
      return false;
    }

    /// <summary>
    /// Sends the given file.
    /// </summary>
    /// <param name="context">Information about request.</param>
    /// <returns>true if the file was sent.</returns>
    private async Task<bool> SendFileAsync(HttpContext context)
    {
      if (TryGetFullPath(context, out var fullPath))
      {
        await context.Response.SendFileAsync(fullPath);
        return true;
      }
      return false;
    }

    private const string HtmlTemplate = @"<!DOCTYPE html>
<html lang=""en"">
<head>
  <meta charset=""utf-8"" />
  <title>File manager</title>
  <style>
    .flex,.flex-column,.flex-end,.flex-wrap,body{display:flex}body{margin:0;font-family:""Segoe UI"",""Segoe WP"",""Helvetica Neue"",RobotoRegular,sans-serif;font-size:16px;line-height:1.2;justify-content:center;background-color:#f0f2f4}.link{cursor:pointer;font-weight:600;color:#247aa8;text-decoration:underline}.link:hover{color:#1b5b7e}.flex{align-items:center}.flex-wrap{flex-wrap:wrap}.flex-end{justify-content:end}.flex-column{flex-direction:column}.margin-bottom{margin-bottom:6px}.padding-12{padding:12px 18px}.round{border-radius:5px}.responsive{display:block;overflow-x:auto}.main{margin:30px 12px 0;border:1px solid #c3cdd5;background-color:#fff;max-width:800px;width:99%}.main-header{justify-content:space-between;font-weight:400;font-size:20px}.table{text-align:left;border-collapse:collapse;border-spacing:0px;width:100%;min-width:500px}.table tr:nth-child(2n){background-color:#f8f8f8}.table th{background-color:#303b44;color:#fff;font-weight:400;padding:6px 12px}.table td{border:1px solid #4e606e;padding:6px 12px}.table td:first-child{border-left:0}.table td:last-child{border-right:0px;text-align:center;padding:3px 12px}.icon{width:20px;height:20px;margin-right:6px}.icon-dir{background:url(""data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' height='20px' viewBox='0 0 24 24' width='20px' fill='%231b657e'><path d='M0 0h24v24H0z' fill='none'/><path d='M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z'/></svg>"") no-repeat}.icon-file{background:url(""data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' height='20px' viewBox='0 0 24 24' width='20px' fill='%231b657e'><path d='M0 0h24v24H0V0z' fill='none'/><path d='M8 16h8v2H8zm0-4h8v2H8zm6-10H6c-1.1 0-2 .9-2 2v16c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8l-6-6zm4 18H6V4h7v5h5v11z'/></svg>"") no-repeat}.btn{color:#fff;display:inline-block;padding:4px 8px;vertical-align:middle;overflow:hidden;text-decoration:none;text-align:center;cursor:pointer;white-space:nowrap;border:0}.btn-close,.details-summary{cursor:pointer;color:#303b44}.btn-edit{background-color:#ee9950;margin-right:5px}.btn-edit:hover{background-color:#ee8a33}.btn-delete{background-color:#ff6f69}.btn-delete:hover{background-color:#fc5650}.btn-primary{background-color:#708495;width:100%;margin-bottom:5px}.btn-primary:hover{background-color:#5d768b}.btn-submit{width:100px}.btn-close{font-size:28px}.details{position:relative}.details-summary{min-width:100px;padding:6px 12px;background:#fff}.details summary>*{display:inline}.details-dropdown{position:absolute;top:40px;padding:4px 4px 0;background:#f0f2f4;border:1px solid #303b44}.modal{display:none;position:fixed;z-index:1;padding-top:100px;left:0;top:0;width:100%;height:100%;overflow:auto;background-color:rgba(0,0,0,.4)}.modal-content{position:relative;background-color:#fefefe;margin:auto;max-width:750px;box-shadow:0 4px 8px 0 rgba(0,0,0,.2),0 6px 20px 0 rgba(0,0,0,.19)}.modal-input{font-family:inherit;font-size:inherit;padding:4px;display:block;border:1px solid #c3cdd5}.modal-textarea{resize:vertical}
  </style>
</head>
<body>
  <div class=""main round"">
    <header class=""flex main-header padding-12"">
      <div id=""header-link"" class=""flex-wrap""></div>
      <details class=""details"">
        <summary class=""details-summary round"">
          <div>Actions</div>
        </summary>
        <div class=""details-dropdown round"">
          <button data-modal=""modal-new-file"" class=""btn btn-primary btn-open""> Add file </button>
          <button data-modal=""modal-folder"" class=""btn btn-primary btn-open""> Add folder </button>
          <button data-modal=""modal-upload"" class=""btn btn-primary btn-open""> Upload files </button>
          <button class=""btn btn-primary"" onclick=""downloadZip()""> Download </button>
        </div>
      </details>
    </header>
    <div class=""padding-12"">
      <div class=""responsive"">
        <table class=""table"">
          <thead>
            <tr>
              <th>Name</th>
              <th>Size</th>
              <th>Last Modified</th>
              <th>Actions</th>
            </tr>
          </thead>
          <tbody id=""tbody""></tbody>
        </table>
      </div>
    </div>
  </div>
  <div id=""modal-new-file"" class=""modal"">
    <div class=""modal-content round"">
      <div class=""padding-12"">
        <div class=""flex-end"">
          <span class=""btn-close"">&times;</span>
        </div>
        <div class=""flex-column"">
          <label class=""margin-bottom"">File name</label>
          <input name=""Name"" type=""text"" autocomplete=""off"" class=""modal-input margin-bottom"" />
          <label class=""margin-bottom"">File text</label>
          <textarea name=""Text"" class=""modal-input modal-textarea margin-bottom"" rows=""10""></textarea>
          <button data-api=""/file"" class=""btn btn-primary btn-submit round""> Save </button>
        </div>
      </div>
    </div>
  </div>
  <div id=""modal-edit-file"" class=""modal"">
    <div class=""modal-content round"">
      <div class=""padding-12"">
        <div class=""flex-end"">
          <span class=""btn-close"">&times;</span>
        </div>
        <div class=""flex-column"">
          <label class=""margin-bottom"">File name</label>
          <input id=""file-name"" name=""Name"" type=""text"" class=""modal-input margin-bottom"" disabled />
          <label class=""margin-bottom"">File text</label>
          <textarea id=""file-text"" name=""Text"" class=""modal-input modal-textarea margin-bottom"" rows=""10""></textarea>
          <button data-api=""/file"" class=""btn btn-primary btn-submit round""> Save </button>
        </div>
      </div>
    </div>
  </div>
  <div id=""modal-folder"" class=""modal"">
    <div class=""modal-content round"">
      <div class=""padding-12"">
        <div class=""flex-end"">
          <span class=""btn-close"">&times;</span>
        </div>
        <div class=""flex-column"">
          <label class=""margin-bottom"">Folder name</label>
          <input name=""Name"" type=""text"" class=""modal-input margin-bottom"" />
          <button data-api=""/folder"" class=""btn btn-primary btn-submit round""> Save </button>
        </div>
      </div>
    </div>
  </div>
  <div id=""modal-upload"" class=""modal"">
    <div class=""modal-content round"">
      <div class=""padding-12"">
        <div class=""flex-end"">
          <span class=""btn-close"">&times;</span>
        </div>
        <div class=""flex-column"">
          <label class=""margin-bottom"">Upload files</label>
          <input name=""Files"" type=""file"" class=""modal-input margin-bottom"" multiple />
          <button data-api=""/upload"" class=""btn btn-primary btn-submit round""> Save </button>
        </div>
      </div>
    </div>
  </div>
  <script>
    const apiUrl=""{@apiUrl}"",fetchGet=async(e,t=null,a=!1)=>{let l=await fetch(apiUrl+e);return l.ok?a?l.text():l.json():t},fetchPost=async(e,t,a={})=>await fetch(apiUrl+e,{method:t,body:a}),getPathParam=()=>new URLSearchParams(window.location.search).get(""path""),updatePathParam=e=>{let t=new URLSearchParams(window.location.search);t.set(""path"",e),history.pushState(null,"""",`${window.location.pathname}?${t.toString()}`)},displayElement=(e,t=""block"")=>{e.style.display=t},deleteFile=async e=>{!0===confirm(`Are you sure you want to delete the ""${e}"" file?`)&&(await fetchPost(`/file?path=${getPathParam()}${e}`,""DELETE""),location.reload())},deleteFolder=async e=>{!0===confirm(`Are you sure you want to delete the ""${e}"" folder?`)&&(await fetchPost(`/folder?path=${getPathParam()}${e}`,""DELETE""),location.reload())},downloadZip=async()=>{!0===confirm(""Are you sure you want to download the current folder?"")&&window.open(`${apiUrl}/download?path=${getPathParam()}`,""_blank"")},openFile=async e=>{window.open(`${apiUrl}/view?path=${e}`,""_blank"")},header=document.getElementById(""header-link""),tableBody=document.getElementById(""tbody""),editModalInput=document.getElementById(""file-name""),editModalTextarea=document.getElementById(""file-text""),submitBtns=document.getElementsByClassName(""btn-submit""),modals=document.getElementsByClassName(""modal""),openModalBtns=document.getElementsByClassName(""btn-open"");Array.from(modals,e=>{let t=e.querySelectorAll("".btn-close"")[0];t.onclick=()=>displayElement(e,""none"")}),window.onclick=e=>{e.target.classList.contains(""modal"")&&displayElement(e.target,""none"")},Array.from(openModalBtns,e=>{let t=e.dataset.modal;e.onclick=()=>displayElement(modals[t])}),Array.from(submitBtns,e=>{e.onclick=async()=>{let t=new FormData,a=e.parentNode.querySelectorAll(""input, textarea"");a.forEach(e=>{""file""===e.type?t.append(e.name,e.files[0]):t.append(e.name,e.value)}),await fetchPost(`${e.dataset.api}?path=${getPathParam()}`,""POST"",t),location.reload()}});const openEditModal=async e=>{let t=await fetchGet(`/file?path=${getPathParam()}${e}`,""not found"",!0);editModalTextarea.value=t,editModalInput.value=e,displayElement(modals[""modal-edit-file""])},updateHeader=e=>{let t=e.split(""/""),a="""",l="""";for(let n=0;n<t.length-1;n++)l+=t[n]+""/"",a+=`<div class=""link"" style=""margin-right:5px;"" onclick=""update('${l}')"">${t[n]}/</div>`;header.innerHTML=a},getTableRow=(e,t)=>{if(e.IsDirectory)return`<tr>
<td><div class=""flex link"" onclick=""update('${t+e.Name}/')"">
    <i class=""icon icon-dir""></i><div>${e.Name}/</div>
</div></td>
<td></td>
<td>${new Date(e.LastModified).toLocaleString()}</td>
<td><button class=""btn btn-delete round"" onclick=""deleteFolder('${e.Name}')"">delete</button></td></tr>`;let a=e.Name.endsWith("".html"")||e.Name.endsWith("".txt"")||e.Name.endsWith("".css"")||e.Name.endsWith("".js"")?`<button class=""btn btn-edit round"" onclick=""openEditModal('${e.Name}')"">edit</button>`:"""";return`<tr>
<td><div class=""flex link"" onclick=""openFile('${t+e.Name}')"">
    <i class=""icon icon-file""></i><div>${e.Name}</div>
</div></td>
<td>${e.Length}</td>
<td>${new Date(e.LastModified).toLocaleString()}</td>
<td>${a}<button class=""btn btn-delete round"" onclick=""deleteFile('${e.Name}')"">delete</button></td></tr>`},update=async e=>{updatePathParam(e),updateHeader(e),tableBody.innerHTML="""";let t=await fetchGet(`/files?path=${e}`,[]);t.sort(e=>e.IsDirectory?-1:1),t.forEach(t=>{tableBody.insertAdjacentHTML(""beforeend"",getTableRow(t,e))})},initPath=getPathParam()||""/"";update(initPath);
  </script>
</body>
</html>";
  }
}
