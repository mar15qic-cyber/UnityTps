using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using UnityFps.Api.Common;
using UnityFps.Api.Data;
using UnityFps.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddControllers();
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "请求参数无效",
            Detail = "请修正标记的字段后重试."
        };
        problem.Extensions["code"] = ApiErrorCodes.ValidationFailed;
        return new BadRequestObjectResult(problem);
    };
});

var connectionString = builder.Configuration.GetConnectionString("GameDb");
if (string.IsNullOrWhiteSpace(connectionString))
{
    connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__GameDb");
}

var allowInMemoryFallback = builder.Environment.IsDevelopment()
    || builder.Configuration.GetValue("Database:AllowInMemoryFallback", false);
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString), mysql => mysql.EnableRetryOnFailure()));
}
else if (allowInMemoryFallback)
{
    builder.Services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase("UnityFpsDevelopmentFallback"));
}
else
{
    throw new InvalidOperationException("未配置 ConnectionStrings:GameDb；生产环境禁止静默降级到 InMemory。");
}

var jwtKey = builder.Configuration["Jwt:SigningKey"] ?? Environment.GetEnvironmentVariable("Jwt__SigningKey");
if (string.IsNullOrWhiteSpace(jwtKey))
{
    jwtKey = "development-only-signing-key-change-me-please-32-bytes";
}

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "UnityFps.Api";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "UnityFps.Client";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddScoped<IProgressionRules, DemoProgressionRules>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ProfileService>();
builder.Services.AddScoped<LoadoutService>();
builder.Services.AddScoped<CommerceService>();
builder.Services.AddScoped<MatchService>();
builder.Services.AddScoped<RoomService>();
builder.Services.AddScoped<PassService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Unity FPS API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>()
    });
});

var app = builder.Build();
// ApiExceptionMiddleware 自带完整异常翻译（ApiException→业务码；其他→500 兜底），
// 不再叠 UseExceptionHandler——.NET8 无参重载会吞异常写 generic 500，
// 导致 ApiController 内抛出的业务异常（404/409）被错误降级（JoinUnknown 房间案）。
app.UseMiddleware<ApiExceptionMiddleware>();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseAuthentication();
app.UseAuthorization();

// 健康检查：Unity 端 Boot 依赖此端点探活（无鉴权），返回数据库提供方标识供大厅状态栏展示。
app.MapGet("/health", (AppDbContext db) => Results.Ok(new
{
    status = "ok",
    database = db.Database.IsRelational() ? "mysql" : "inmemory"
}));

app.MapControllers();

if (!string.IsNullOrWhiteSpace(connectionString) || allowInMemoryFallback)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsRelational()) await db.Database.MigrateAsync();
    else await db.Database.EnsureCreatedAsync();
    await CatalogSeeder.SeedAsync(db);
    await DemoSeeder.SeedAsync(scope.ServiceProvider, builder.Configuration);
    await PassSeeder.SeedAsync(db);
}

app.Run();

public partial class Program { }
