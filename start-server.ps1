param([int]$Port = 8080, [switch]$NoBrowser)
# TOKENIFY — tiny local web server (Windows PowerShell, no installs needed).
# Serves this folder at http://localhost:<Port>/ and opens your browser (unless -NoBrowser). Close the window to stop.
$root = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
$types = @{
  '.html' = 'text/html; charset=utf-8'; '.js' = 'text/javascript; charset=utf-8'; '.css' = 'text/css; charset=utf-8';
  '.png' = 'image/png'; '.webp' = 'image/webp'; '.svg' = 'image/svg+xml'; '.json' = 'application/json';
  '.webmanifest' = 'application/manifest+json'; '.ico' = 'image/x-icon'; '.txt' = 'text/plain; charset=utf-8';
  '.md' = 'text/plain; charset=utf-8'; '.sql' = 'text/plain; charset=utf-8'; '.woff2' = 'font/woff2'
}
$listener = New-Object System.Net.HttpListener
for ($try = 0; $try -lt 10; $try++) {
  try { $listener = New-Object System.Net.HttpListener; $listener.Prefixes.Add("http://localhost:$Port/"); $listener.Start(); break }
  catch { $Port++ }
}
if (-not $listener.IsListening) { Write-Host 'Could not start a local server.'; exit 1 }
$url = "http://localhost:$Port/"
Write-Host ""
Write-Host "  TOKENIFY is running at $url"
Write-Host "  Leave this window open while you use it. Close it to stop the server."
Write-Host ""
if (-not $NoBrowser) { Start-Process $url }
while ($listener.IsListening) {
  try { $ctx = $listener.GetContext() } catch { break }
  $res = $ctx.Response
  try {
    $rel = [Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath).TrimStart('/')
    if ($rel -eq '' -or $rel.EndsWith('/')) { $rel += 'index.html' }
    $full = [IO.Path]::GetFullPath((Join-Path $root $rel))
    if (-not $full.StartsWith($root)) { $res.StatusCode = 403 }
    elseif (Test-Path -LiteralPath $full -PathType Leaf) {
      $bytes = [IO.File]::ReadAllBytes($full)
      $ext = [IO.Path]::GetExtension($full).ToLower()
      $res.ContentType = $(if ($types[$ext]) { $types[$ext] } else { 'application/octet-stream' })
      $res.ContentLength64 = $bytes.Length
      $res.OutputStream.Write($bytes, 0, $bytes.Length)
    }
    else { $res.StatusCode = 404 }
  }
  catch { try { $res.StatusCode = 500 } catch {} }
  finally { try { $res.Close() } catch {} }
}
