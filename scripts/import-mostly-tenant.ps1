# Import Markdown files into the "mostly" tenant for LucidRAG
# Usage: .\import-mostly-tenant.ps1

$ErrorActionPreference = "Stop"

$BaseUrl = "http://localhost:5019"
$TenantId = "mostly"
$SourceDir = "C:\Blog\mostlylucidweb\Mostlylucid\Markdown"
$Headers = @{
    "X-Tenant-Id" = $TenantId
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "LucidRAG Tenant Import: $TenantId" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check if LucidRAG is running
try {
    $health = Invoke-RestMethod -Uri "$BaseUrl/healthz" -Method Get -TimeoutSec 5
    Write-Host "[OK] LucidRAG is running" -ForegroundColor Green
} catch {
    Write-Host "[ERROR] LucidRAG is not running at $BaseUrl" -ForegroundColor Red
    Write-Host "Start it with: dotnet run --project src/LucidRAG/LucidRAG.csproj" -ForegroundColor Yellow
    exit 1
}

# Step 1: Provision the tenant
Write-Host ""
Write-Host "Step 1: Provisioning tenant '$TenantId'..." -ForegroundColor Yellow

try {
    $existingTenant = Invoke-RestMethod -Uri "$BaseUrl/api/tenants/$TenantId" -Method Get -ErrorAction SilentlyContinue
    Write-Host "[OK] Tenant '$TenantId' already exists" -ForegroundColor Green
} catch {
    try {
        $tenantRequest = @{
            TenantId = $TenantId
            DisplayName = "Mostly Lucid Blog"
            Plan = "pro"
        } | ConvertTo-Json

        $tenant = Invoke-RestMethod -Uri "$BaseUrl/api/tenants" -Method Post -Body $tenantRequest -ContentType "application/json"
        Write-Host "[OK] Created tenant: $($tenant.tenantId)" -ForegroundColor Green
    } catch {
        if ($_.Exception.Response.StatusCode -eq 409) {
            Write-Host "[OK] Tenant already exists" -ForegroundColor Green
        } else {
            Write-Host "[ERROR] Failed to create tenant: $_" -ForegroundColor Red
            exit 1
        }
    }
}

# Step 2: Create a collection for the blog posts
Write-Host ""
Write-Host "Step 2: Creating collection 'blog-posts'..." -ForegroundColor Yellow

try {
    $collectionRequest = @{
        Name = "blog-posts"
        Description = "Mostly Lucid blog posts and articles"
    } | ConvertTo-Json

    $collection = Invoke-RestMethod -Uri "$BaseUrl/api/collections" -Method Post -Body $collectionRequest -ContentType "application/json" -Headers $Headers
    $collectionId = $collection.id
    Write-Host "[OK] Created collection: $collectionId" -ForegroundColor Green
} catch {
    if ($_.Exception.Response.StatusCode -eq 409) {
        # Collection exists, get its ID
        $collections = Invoke-RestMethod -Uri "$BaseUrl/api/collections" -Method Get -Headers $Headers
        $existingCollection = $collections | Where-Object { $_.name -eq "blog-posts" }
        if ($existingCollection) {
            $collectionId = $existingCollection.id
            Write-Host "[OK] Collection already exists: $collectionId" -ForegroundColor Green
        }
    } else {
        Write-Host "[WARN] Could not create collection: $_" -ForegroundColor Yellow
        $collectionId = $null
    }
}

# Step 3: Get list of markdown files (excluding summaries and translations)
Write-Host ""
Write-Host "Step 3: Scanning markdown files in $SourceDir..." -ForegroundColor Yellow

$mdFiles = Get-ChildItem -Path $SourceDir -Filter "*.md" -File |
    Where-Object { $_.Name -notmatch "\.summary\.md$" }

Write-Host "Found $($mdFiles.Count) markdown files to import" -ForegroundColor Cyan

# Step 4: Import files with change detection
Write-Host ""
Write-Host "Step 4: Importing files to tenant '$TenantId' with change detection..." -ForegroundColor Yellow

$created = 0
$updated = 0
$unchanged = 0
$failed = 0
$total = $mdFiles.Count
$processed = 0

foreach ($file in $mdFiles) {
    $processed++
    $pct = [Math]::Round(($processed / $total) * 100)
    Write-Host "`r[$pct%] Processing $processed/$total - $($file.Name)                    " -NoNewline

    try {
        $uri = "$BaseUrl/api/documents/import"

        # Build form data with source metadata
        $boundary = [System.Guid]::NewGuid().ToString()
        $contentType = "multipart/form-data; boundary=$boundary"

        $fileBytes = [System.IO.File]::ReadAllBytes($file.FullName)
        $fileEnc = [System.Text.Encoding]::GetEncoding("iso-8859-1").GetString($fileBytes)

        # Get file timestamps
        $fileInfo = Get-Item $file.FullName
        $createdAt = $fileInfo.CreationTimeUtc.ToString("o")
        $modifiedAt = $fileInfo.LastWriteTimeUtc.ToString("o")
        $sourcePath = $file.FullName.Replace('\', '/')

        $bodyLines = @(
            "--$boundary",
            "Content-Disposition: form-data; name=`"file`"; filename=`"$($file.Name)`"",
            "Content-Type: text/markdown",
            "",
            $fileEnc,
            "--$boundary",
            "Content-Disposition: form-data; name=`"sourcePath`"",
            "",
            $sourcePath,
            "--$boundary",
            "Content-Disposition: form-data; name=`"sourceCreatedAt`"",
            "",
            $createdAt,
            "--$boundary",
            "Content-Disposition: form-data; name=`"sourceModifiedAt`"",
            "",
            $modifiedAt
        )

        if ($collectionId) {
            $bodyLines += @(
                "--$boundary",
                "Content-Disposition: form-data; name=`"collectionId`"",
                "",
                $collectionId
            )
        }

        $bodyLines += "--$boundary--"
        $body = $bodyLines -join "`r`n"

        $result = Invoke-RestMethod -Uri $uri -Method Post -Body $body -ContentType $contentType -Headers $Headers

        switch ($result.action) {
            "created" { $created++ }
            "updated" { $updated++ }
            "unchanged" { $unchanged++ }
        }
    } catch {
        $failed++
        Write-Host "`n[WARN] Failed to import $($file.Name): $_" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Import Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Created:   $created files" -ForegroundColor Green
Write-Host "Updated:   $updated files" -ForegroundColor $(if ($updated -gt 0) { "Yellow" } else { "Green" })
Write-Host "Unchanged: $unchanged files" -ForegroundColor Cyan
Write-Host "Failed:    $failed files" -ForegroundColor $(if ($failed -gt 0) { "Red" } else { "Green" })
Write-Host ""
Write-Host "Access your tenant at:" -ForegroundColor Cyan
Write-Host "  Local:     $BaseUrl/t/$TenantId" -ForegroundColor White
Write-Host "  Subdomain: http://$TenantId.lucidrag.com (when deployed)" -ForegroundColor White
Write-Host ""
Write-Host "Re-run this script anytime to sync changes!" -ForegroundColor DarkGray
