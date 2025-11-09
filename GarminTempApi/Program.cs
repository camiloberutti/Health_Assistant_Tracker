using System.IO;
using GarminTempApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddRazorPages();
builder.Services.AddOpenApi();

// Determine application data directory for the local SQLite database
var dataDirectory = Environment.GetEnvironmentVariable("APP_DATA_DIR")
                    ?? builder.Configuration["Data:Directory"]
                    ?? Path.Combine(builder.Environment.ContentRootPath, "data");

Directory.CreateDirectory(dataDirectory);
var sqlitePath = Path.Combine(dataDirectory, "garmin_app.db");

// Register Garmin Connect importer and synchronization services
builder.Services.AddSingleton<GarminConnectImporter>();
builder.Services.AddSingleton<GarminDataSyncService>();
builder.Services.AddHostedService<GarminSyncHostedService>();

// Register EF Core sqlite (local app DB)
builder.Services.AddDbContext<GarminTempApi.Data.AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? $"Data Source={sqlitePath}"));

// Register other services used by project
builder.Services.AddScoped<GarminTempApi.Services.ActivityService>();
builder.Services.AddScoped<GarminTempApi.Services.IActivityParser, GarminTempApi.Services.GpxTcxParser>();
builder.Services.AddScoped<GarminTempApi.Services.FitActivityParser>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GarminTempApi.Data.AppDbContext>();
    db.Database.EnsureCreated();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.MapOpenApi();
}

app.UseStaticFiles();
app.UseRouting();
app.UseHttpsRedirection();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();

app.Run();
