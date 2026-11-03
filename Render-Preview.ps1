param([string]$OutputPath = (Join-Path $PSScriptRoot 'desktop-preview.png'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
[xml]$document = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\PeisSelfPrint.Desktop\MainWindow.xaml') -Raw -Encoding UTF8
$document.DocumentElement.RemoveAttribute('Class', 'http://schemas.microsoft.com/winfx/2006/xaml')
foreach ($element in $document.SelectNodes('//*')) {
    foreach ($attribute in @('Click', 'KeyDown', 'TextChanged')) { $element.RemoveAttribute($attribute) }
}
$document.DocumentElement.SetAttribute('WindowState', 'Normal')
$reader = New-Object System.Xml.XmlNodeReader $document
$window = [Windows.Markup.XamlReader]::Load($reader)
try {
    $window.Show()
    $window.UpdateLayout()
    $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Render)
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap 1080,780,96,96,([Windows.Media.PixelFormats]::Pbgra32)
    $background = New-Object Windows.Media.DrawingVisual
    $drawing = $background.RenderOpen()
    $drawing.DrawRectangle([Windows.Media.BrushConverter]::new().ConvertFromString('#F3F6FB'), $null, [Windows.Rect]::new(0,0,1080,780))
    $drawing.Close()
    $bitmap.Render($background)
    $bitmap.Render($window)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create($OutputPath)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
    Write-Output $OutputPath
} finally { $window.Close() }
