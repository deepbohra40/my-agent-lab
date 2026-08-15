// Aspire boilerplate — copied verbatim on purpose.
// Note the file name: Aspire 13 renamed Program.cs to AppHost.cs, and the
// .csproj uses Sdk="Aspire.AppHost.Sdk/..." rather than a package reference.
//
// This project is the startup project. F5 here launches WebApi and opens the
// Aspire dashboard; it is not in the request path at runtime.

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.WebApi>("webapi")
    .WithUrls(context =>
    {
        // Adds a convenience link to DevUI on the dashboard's resource row.
        // DevUI itself is a MAF package, not Aspire — this just links to it.
        var baseUrl = context.Urls.FirstOrDefault();
        if (baseUrl is not null)
        {
            context.Urls.Add(new()
            {
                Url = baseUrl.Url.TrimEnd('/') + "/devui",
                DisplayText = "DevUI Visual App"
            });
        }
    });

builder.Build().Run();
