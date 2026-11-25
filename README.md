# MY QABAYEL Admin

Separate .NET 8 MVC administration portal for Al Qabayel Discount Center's loyalty programme. It uses the `QABAYEL` loyalty group, the `MyQabayelAdminDB` admin database, and the `ZWCORE` ERP database. Its login and cookies are separate from the Qabayel Mall admin portal.

## Open and build

Open `MyQabayel.Admin.sln` in Visual Studio, or run:

```powershell
dotnet restore MyQabayel.Admin.sln
dotnet build MyQabayel.Admin.sln
```

The project requires an existing SQL Server database with the MY QABAYEL admin schema and access to the ERP loyalty group. Database schema deployment is managed separately from this application repository.

## Configure locally

Copy `appsettings.example.json` to `appsettings.json`, then set the `AdminDb` and `ErpDb` connection strings and the public URL for your environment. The real settings file is ignored by Git. Keep Firebase service-account files outside `wwwroot` and out of this repository. Push notifications remain unavailable until their credential paths and project IDs are configured for this deployment.

The configured loyalty group is locked to `QABAYEL`. Confirm that this group is active in `ZWCORE` before running the portal.

```powershell
dotnet run --project MyQabayel.Admin.csproj --launch-profile http
```

The `http` launch profile currently uses port `5014`; adjust its host address for your machine if needed.

## Administrator access

Create the first administrator using the built-in command. It prompts for an email and password, creates the `MyQabayelAdmin` role, and assigns the user to that role.

```powershell
dotnet run --project MyQabayel.Admin.csproj -- --create-admin
```

Use the **Change Password** page after the first sign-in. No administrator password or database credential is committed to this repository.
