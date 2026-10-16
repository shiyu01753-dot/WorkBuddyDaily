param([string]$OutputName='WorkBuddyDaily.exe')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$frames=@()
foreach($size in @(16,20,24,32,40,48,64,128,256)) {
    $bitmap=[Drawing.Bitmap]::new($size,$size)
    $g=[Drawing.Graphics]::FromImage($bitmap); $g.SmoothingMode='AntiAlias';$g.ScaleTransform($size/64.0,$size/64.0)
    $g.Clear([Drawing.Color]::Transparent)
    $fill=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(35,105,232))
    $g.FillEllipse($fill,0,0,63,63)
    $pen=[Drawing.Pen]::new([Drawing.Color]::White,3.2);$pen.StartCap='Round';$pen.EndCap='Round';$pen.LineJoin='Round'
    $g.DrawRectangle($pen,17,19,30,29);$g.DrawLine($pen,17,28,47,28);$g.DrawLine($pen,24,14,24,23);$g.DrawLine($pen,40,14,40,23)
    $g.DrawLines($pen,[Drawing.PointF[]]@([Drawing.PointF]::new(24,37),[Drawing.PointF]::new(30,43),[Drawing.PointF]::new(41,33)))
    $stream=[IO.MemoryStream]::new();$bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png);$frames+=,@{size=$size;data=$stream.ToArray()}
    $stream.Dispose();$pen.Dispose();$fill.Dispose();$g.Dispose();$bitmap.Dispose()
}
$icon=[IO.MemoryStream]::new();$writer=[IO.BinaryWriter]::new($icon)
$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count);$offset=6+16*$frames.Count
foreach($frame in $frames){$dimension=if($frame.size -eq 256){0}else{$frame.size};$writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frame.data.Length);$writer.Write([uint32]$offset);$offset+=$frame.data.Length}
foreach($frame in $frames){$writer.Write([byte[]]$frame.data)}
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'daily.ico'),$icon.ToArray());$writer.Dispose();$icon.Dispose()
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf=Join-Path $framework 'WPF'
$parameters=New-Object System.CodeDom.Compiler.CompilerParameters
$parameters.GenerateExecutable=$true
$parameters.OutputAssembly=Join-Path $PSScriptRoot $OutputName
$parameters.CompilerOptions='/target:winexe /optimize+ /platform:anycpu /win32manifest:"'+(Join-Path $PSScriptRoot 'app.manifest')+'" /win32icon:"'+(Join-Path $PSScriptRoot 'daily.ico')+'"'
foreach($name in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Security.dll','System.Net.Http.dll','System.Xaml.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')) {
    $reference=Join-Path $framework $name
    if(-not(Test-Path -LiteralPath $reference)){$reference=Join-Path $wpf $name}
    if(-not(Test-Path -LiteralPath $reference)){throw ('Missing framework assembly: '+$name)}
    [void]$parameters.ReferencedAssemblies.Add($reference)
}
$provider=New-Object Microsoft.CSharp.CSharpCodeProvider
$sources=@('Core.cs','App.cs','Tests.cs') | ForEach-Object {Join-Path $PSScriptRoot $_}
$compiled=$provider.CompileAssemblyFromFile($parameters,[string[]]$sources)
if($compiled.Errors.HasErrors){$compiled.Errors | ForEach-Object {Write-Output $_.ToString()};throw 'Compilation failed'}
$provider.Dispose()
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.config') -Destination (Join-Path $PSScriptRoot ($OutputName+'.config')) -Force
Write-Output 'Built high-DPI native desktop application.'
