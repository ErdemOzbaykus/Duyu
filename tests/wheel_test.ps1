# Scrolling over a focused, closed ComboBox must scroll the page, not change the choice.
param([Parameter(Mandatory)][string]$Exe)

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$app = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Resolve-Path $Exe)))
[void]$app.GetType('Duyu.Kit').GetMethod('WheelScrollsPage').Invoke($null, @())

$window = New-Object Windows.Window -Property @{ Width = 300; Height = 160; Left = -9000; Top = 0; WindowStartupLocation = 'Manual'; ShowActivated = $false; ShowInTaskbar = $false }
$scroll = New-Object Windows.Controls.ScrollViewer
$stack = New-Object Windows.Controls.StackPanel
$box = New-Object Windows.Controls.ComboBox
foreach ($item in 'Otomatik', 'CABLE Input', 'Monitor', 'Headset') { [void]$box.Items.Add($item) }
$box.SelectedIndex = 3
[void]$stack.Children.Add($box)
[void]$stack.Children.Add((New-Object Windows.Controls.Border -Property @{ Height = 900 }))
$scroll.Content = $stack
$window.Content = $scroll
$window.Show()
[void]$box.Focus()
$window.UpdateLayout()

# the same tunnel + bubble pair a real wheel notch produces
function Send-Wheel([int]$delta) {
    $preview = New-Object Windows.Input.MouseWheelEventArgs ([Windows.Input.Mouse]::PrimaryDevice), 0, $delta
    $preview.RoutedEvent = [Windows.UIElement]::PreviewMouseWheelEvent
    $box.RaiseEvent($preview)
    if (-not $preview.Handled) {
        $bubble = New-Object Windows.Input.MouseWheelEventArgs ([Windows.Input.Mouse]::PrimaryDevice), 0, $delta
        $bubble.RoutedEvent = [Windows.UIElement]::MouseWheelEvent
        $box.RaiseEvent($bubble)
    }
}

$focused = $box.IsKeyboardFocusWithin
1..3 | ForEach-Object { Send-Wheel 120 }
$kept = $box.SelectedIndex -eq 3
Send-Wheel (-120)
$window.UpdateLayout()
$scrolled = $scroll.VerticalOffset -gt 0
$window.Close()

if ($focused -and $kept -and $scrolled) { 'wheel: PASS (choice kept, page scrolled)'; exit 0 }
"wheel: FAIL focused=$focused kept=$kept scrolled=$scrolled"
exit 1
