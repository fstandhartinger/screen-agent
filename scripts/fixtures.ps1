Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force fixtures | Out-Null
foreach ($size in @(24,36,48)) {
  $bmp = [Drawing.Bitmap]::new(1600,280)
  $g = [Drawing.Graphics]::FromImage($bmp)
  $g.Clear([Drawing.Color]::White)
  $g.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
  $font = [Drawing.Font]::new('Arial',$size,[Drawing.FontStyle]::Regular,[Drawing.GraphicsUnit]::Pixel)
  $g.DrawString('Projektion der freien Reserve  a#332179',$font,[Drawing.Brushes]::Black,30,35)
  $g.DrawString('A #b8c3ff',$font,[Drawing.Brushes]::Black,30,130)
  $bmp.Save((Join-Path $PWD "fixtures/ocr-$size.png"),[Drawing.Imaging.ImageFormat]::Png)
  $font.Dispose(); $g.Dispose(); $bmp.Dispose()
}
