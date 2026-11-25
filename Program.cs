using QabayelMall.API.Data;
using QabayelMall.API.Models;
using QabayelMall.API.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
const string adminRole = "MyQabayelAdmin";
builder.Services.AddDataProtection().SetApplicationName("MyQabayel.Admin.Web");
builder.Services.AddControllersWithViews(options =>
{
    var authenticatedUsersOnly = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireRole(adminRole)
        .Build();
    options.Filters.Add(new AuthorizeFilter(authenticatedUsersOnly));
});
builder.Services.AddDbContext<AdminDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("AdminDb")));
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = true;
    options.User.RequireUniqueEmail = true;
    options.Lockout.AllowedForNewUsers = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddEntityFrameworkStores<AdminDbContext>().AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.Cookie.Name = "MyQabayel.Admin.Web.Auth";
    options.Cookie.Path = "/";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    if (Uri.TryCreate(builder.Configuration["PublicBaseUrl"], UriKind.Absolute, out var publicBaseUrl)
        && publicBaseUrl.Scheme == Uri.UriSchemeHttp)
    {
        options.Cookie.SecurePolicy = CookieSecurePolicy.None;
    }
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
});
builder.Services.AddScoped<ErpPublishingService>();
builder.Services.AddScoped<CoreEdgeCustomerService>();
builder.Services.AddScoped<EngagementMonitorService>();
builder.Services.AddScoped<CustomerAppManagementService>();
builder.Services.AddScoped<LoyaltyCustomerAudience>();
builder.Services.AddScoped<LoyaltyCurrencyService>();
builder.Services.AddScoped<PromoNotificationService>();
builder.Services.AddScoped<CustomerPromoService>();
builder.Services.AddScoped<PromoEditDraftService>();
builder.Services.AddScoped<PromoCampaignContentService>();
builder.Services.AddScoped<PromoImageStorage>();
builder.Services.AddSingleton<FirebasePushSender>();
builder.Services.AddHostedService<NotificationPushWorker>();
builder.Services.AddSingleton<PublicAssetUrlService>();

var app = builder.Build();

if (args.Contains("--create-admin", StringComparer.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

    Console.Write("Administrator email: ");
    var email = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(email))
    {
        Console.Error.WriteLine("A valid administrator email is required.");
        return;
    }

    if (await userManager.FindByEmailAsync(email) is not null)
    {
        Console.Error.WriteLine("An Identity user with that email already exists.");
        return;
    }

    Console.Write("Administrator password: ");
    var password = ReadHiddenValue();
    Console.Write("Confirm password: ");
    var confirmation = ReadHiddenValue();
    if (password != confirmation)
    {
        Console.Error.WriteLine("Passwords do not match.");
        return;
    }

    if (!await roleManager.RoleExistsAsync(adminRole))
    {
        var roleResult = await roleManager.CreateAsync(new IdentityRole(adminRole));
        if (!roleResult.Succeeded)
        {
            foreach (var error in roleResult.Errors) Console.Error.WriteLine(error.Description);
            return;
        }
    }

    var user = new ApplicationUser
    {
        UserName = email,
        Email = email,
        EmailConfirmed = true,
        IsActive = true,
        DisplayName = "My Qabayel Administrator"
    };
    var result = await userManager.CreateAsync(user, password);
    if (!result.Succeeded)
    {
        foreach (var error in result.Errors) Console.Error.WriteLine(error.Description);
        return;
    }

    var assignment = await userManager.AddToRoleAsync(user, adminRole);
    if (!assignment.Succeeded)
    {
        foreach (var error in assignment.Errors) Console.Error.WriteLine(error.Description);
        await userManager.DeleteAsync(user);
        return;
    }

    Console.WriteLine("Administrator created successfully. The password was not displayed or logged.");
    return;
}

if (args.Contains("--reset-admin-password", StringComparer.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

    Console.Write("Administrator email: ");
    var email = Console.ReadLine()?.Trim();
    var user = string.IsNullOrWhiteSpace(email) ? null : await userManager.FindByEmailAsync(email);
    if (user is null || !await userManager.IsInRoleAsync(user, adminRole))
    {
        Console.Error.WriteLine("Administrator account not found.");
        return;
    }

    Console.Write("New password: ");
    var password = ReadHiddenValue();
    Console.Write("Confirm new password: ");
    if (password != ReadHiddenValue())
    {
        Console.Error.WriteLine("Passwords do not match.");
        return;
    }

    var token = await userManager.GeneratePasswordResetTokenAsync(user);
    var result = await userManager.ResetPasswordAsync(user, token, password);
    if (!result.Succeeded)
    {
        foreach (var error in result.Errors) Console.Error.WriteLine(error.Description);
        return;
    }

    Console.WriteLine("Administrator password reset successfully. The password was not displayed or logged.");
    return;
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.Run();

static string ReadHiddenValue()
{
    var value = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return value.ToString();
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (value.Length > 0) value.Length--;
            continue;
        }
        if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
    }
}
