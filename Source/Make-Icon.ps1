param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$images=@()
foreach($size in @(16,32,48,64,128,256)){
 $bitmap=New-Object Drawing.Bitmap($size,$size)
 $g=[Drawing.Graphics]::FromImage($bitmap)
 $g.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
 $g.Clear([Drawing.Color]::FromArgb(255,16,21,29))
 $g.ScaleTransform($size/100.0,$size/100.0)
 $brush=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(255,135,226,209))
 $g.FillEllipse($brush,4,4,92,92)
 $pen=New-Object Drawing.Pen([Drawing.Color]::FromArgb(255,9,40,34),6)
 $pen.StartCap=[Drawing.Drawing2D.LineCap]::Round;$pen.EndCap=[Drawing.Drawing2D.LineCap]::Round;$pen.LineJoin=[Drawing.Drawing2D.LineJoin]::Round
 $points=[Drawing.PointF[]]@((New-Object Drawing.PointF(34,31)),(New-Object Drawing.PointF(66,65)),(New-Object Drawing.PointF(50,82)),(New-Object Drawing.PointF(50,18)),(New-Object Drawing.PointF(66,35)),(New-Object Drawing.PointF(34,69)))
 $g.DrawLines($pen,$points)
 $memory=New-Object IO.MemoryStream
 $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
 $images+=,@{Size=$size;Bytes=$memory.ToArray()}
 $memory.Dispose();$pen.Dispose();$brush.Dispose();$g.Dispose();$bitmap.Dispose()
}
$file=[IO.File]::Create($Destination);$writer=New-Object IO.BinaryWriter($file)
try{
 $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$images.Count)
 $offset=6+16*$images.Count
 foreach($entry in $images){$dimension=if($entry.Size -eq 256){0}else{$entry.Size};$writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$entry.Bytes.Length);$writer.Write([uint32]$offset);$offset+=$entry.Bytes.Length}
 foreach($entry in $images){$writer.Write([byte[]]$entry.Bytes)}
}finally{$writer.Dispose();$file.Dispose()}
