$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
$app = New-Object Windows.Application
[Reflection.Assembly]::LoadFrom('E:\Programs\Playnite\Playnite.SDK.dll') | Out-Null
$fork = [Reflection.Assembly]::LoadFrom((Resolve-Path "$PSScriptRoot\..\..\source\bin\Release\PlayniteAchievements.dll").Path)
$core = [Reflection.Assembly]::LoadFrom('E:\Programs\PlaynitePortableClean\Extensions\PS5Core_84d85c00-14b1-4859-805f-6bd9d95fdae9\PS5Core.dll')
$row = [Activator]::CreateInstance($core.GetType('PS5Core.GameAchievementsItem'))
$row.Provider = 'Local'; $row.Total = 25; $row.BronzeCount = 25
$imageXaml = @'
<Image xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Image.Style><Style TargetType="Image"><Setter Property="Visibility" Value="Collapsed"/><Style.Triggers><DataTrigger Binding="{Binding HasPlatinum}" Value="True"><Setter Property="Visibility" Value="Visible"/></DataTrigger><DataTrigger Binding="{Binding HasCapstoneUnlocked}" Value="True"><Setter Property="Visibility" Value="Visible"/></DataTrigger></Style.Triggers></Style></Image.Style></Image>
'@
$image = [Windows.Markup.XamlReader]::Parse($imageXaml)
$image.DataContext = $row
$adapt = $fork.GetType('PlayniteAchievements.Services.ThemeMigration.Ps5TrophyProgressBridge').GetMethod('AdaptCompletionIcon',[Reflection.BindingFlags]'Static,NonPublic')
function Check-Icon($expected) {
    $adapt.Invoke($null,@($image.PSObject.BaseObject))
    $app.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle)
    if ($image.Visibility.ToString() -ne $expected) { throw "Expected $expected, got $($image.Visibility)" }
}
Check-Icon 'Visible'
if ($row.HasPlatinum -or $row.HasCapstoneUnlocked -or $row.PlatinumCount -ne 0) { throw 'Native trophy flags/counts were changed' }
$row.BronzeCount = 24
Check-Icon 'Collapsed'
$row.HasPlatinum = $true
Check-Icon 'Visible'
$row.HasPlatinum = $false; $row.HasCapstoneUnlocked = $true
Check-Icon 'Visible'
$row.HasCapstoneUnlocked = $false; $row.Total = 0; $row.BronzeCount = 0
Check-Icon 'Collapsed'
$nextRow = [Activator]::CreateInstance($core.GetType('PS5Core.GameAchievementsItem'))
$nextRow.Total = 11; $nextRow.BronzeCount = 11
$image.DataContext = $nextRow
Check-Icon 'Visible'
# An image without the completion-trophy triggers must retain its own visibility.
$unrelated = New-Object Windows.Controls.Image
$unrelated.DataContext = $row
$adapt.Invoke($null,@($unrelated.PSObject.BaseObject))
if ($null -ne [Windows.Data.BindingOperations]::GetMultiBinding($unrelated,[Windows.UIElement]::VisibilityProperty)) { throw 'Unrelated image changed' }
'Passed: 25/25 completion trophy, incomplete/empty sets, native platinum/capstone preservation, recycled row, and unrelated image isolation.'
