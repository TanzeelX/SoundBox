var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular",
        policy =>
        {
            policy.WithOrigins("http://localhost:4200") // Angular dev server
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .WithExposedHeaders("Content-Disposition");
        });
});

var app = builder.Build();

app.UseCors("AllowAngular");

//  Always serve static files (Angular build inside wwwroot)
app.UseStaticFiles();

//  Always enable SPA fallback (for Angular routing)
app.MapFallbackToFile("index.html");

// Swagger only in Development (good practice)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
