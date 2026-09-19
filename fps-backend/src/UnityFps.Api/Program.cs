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

// 提供方决策下沉到 DbContext 选项构建时（配置定稿后）：测试工厂经 ConfigureAppConfiguration
// 注入的覆盖值在顶层读取时还不可见，只有在选项构建（首次解析 DbContext）时才可见。
var configuredConnectionString = builder.Configuration.GetConnectionString("GameDb");
if (string.IsNullOrWhiteSpace(configuredConnectionString))
{
    configuredConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__GameDb");
}
var allowInMemoryFallback = builder.Environment.IsDevelopment()
    || builder.Configuration.GetValue("Database:AllowInMemoryFallback", false);
if (string.IsNullOrWhiteSpace(configuredConnectionString) && !allowInMemoryFallback)
{
    throw new InvalidOperationException("未配置 ConnectionStrings:GameDb；生产环境禁止静默降级到 InMemory。");
}

builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    // 测试隔离开关：Database:InMemoryName 存在 → 强制指定名 InMemory 存储。
    var inMemoryName = configuration["Database:InMemoryName"];
    if (inMemoryName is not null)
    {
        options.UseInMemoryDatabase(inMemoryName);
        return;
    }

    var connectionString = configuration.GetConnectionString("GameDb");
    if (string.IsNullOrWhiteSpace(connectionString))
        connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__GameDb");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("未配置 ConnectionStrings:GameDb；生产环境禁止静默降级到 InMemory。");
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString), mysql => mysql.EnableRetryOnFailure());
});

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
        // 单活会话（2026-09-17 实测缺口）：比对 token 的 tv 声明与库中 TokenVersion——
        // 同账号再次登录即顶替（旧 token 一律 401，客户端按 SessionExpired 回登录页）。
        // 声明缺失（部署前签发的存量 token）按版本 0 处理：未重新登录的账号保持有效（向后兼容）。
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var principal = context.Principal;
                var sub = principal?.FindFirst("sub")?.Value;
                if (!long.TryParse(sub, out var userId))
                {
                    context.Fail("token missing sub");
                    return;
                }
                var tokenVersion = long.TryParse(principal!.FindFirst("tv")?.Value, out var tv) ? tv : 0L;
                var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var current = await db.Users.AsNoTracking()
                    .Where(u => u.Id == userId)
                    .Select(u => (long?)u.TokenVersion)
                    .FirstOrDefaultAsync(context.HttpContext.RequestAborted);
                if (current is null || current.Value != tokenVersion)
                    context.Fail("session superseded by newer login");
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddScoped<IProgressionRules, DemoProgressionRules>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ProfileService>();
builder.Services.AddScoped<LoadoutService>();
builder.Services.AddScoped<CommerceService>();
builder.Services.AddScoped<UserSettingsService>();
builder.Services.AddScoped<MatchService>();
builder.Services.AddSingleton<RoomChatService>();
builder.Services.AddScoped<RoomService>();
builder.Services.AddScoped<ServerInstanceService>();
builder.Services.Configure<ServerInstanceOptions>(builder.Configuration.GetSection("ServerInstances"));
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

// 热更分发（2026-09-17 计划 P2）：匿名只读静态文件——客户端 Boot 阶段在登录前拉取，必须无鉴权。
// 目录 fps-backend/hotupdate/（产物目录，不入库）：manifest.json（当前版本指针）+ <version>/<path>（版本目录不可变）。
// 发布 = Tools/HotUpdate/Publish-HotUpdate.ps1 拷入新版本目录并覆盖 manifest.json，无需重启后端（每请求读盘）。
var hotUpdateRoot = Path.Combine(app.Environment.ContentRootPath, "hotupdate");
Directory.CreateDirectory(hotUpdateRoot);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(hotUpdateRoot),
    RequestPath = "/hotupdate",
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
});

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

if (!string.IsNullOrWhiteSpace(configuredConnectionString) || allowInMemoryFallback)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsRelational()) await db.Database.MigrateAsync();
    else await db.Database.EnsureCreatedAsync();
    await CatalogSeeder.SeedAsync(db);
    await DemoSeeder.SeedAsync(scope.ServiceProvider, builder.Configuration);
    await PassSeeder.SeedAsync(db);
    await AttachmentSystemSeeder.SeedAsync(db);
}

app.Run();

public partial class Program { }
