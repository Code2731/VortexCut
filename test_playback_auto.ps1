# VortexCut 재생 자동화 테스트 (PowerShell + UI Automation)

Write-Host "=== VortexCut 재생 자동화 테스트 ===" -ForegroundColor Green

# 1. VortexCut 실행
Write-Host "`n[1/4] VortexCut 앱 실행 중..." -ForegroundColor Yellow
$appPath = "h:\MyProject\VortexCut\VortexCut.UI\bin\Debug\net8.0\VortexCut.UI.exe"

if (!(Test-Path $appPath)) {
    Write-Host "❌ 앱 실행 파일을 찾을 수 없습니다: $appPath" -ForegroundColor Red
    exit 1
}

$process = Start-Process -FilePath $appPath -PassThru
Start-Sleep -Seconds 5  # 앱 로딩 대기

Write-Host "✅ 앱 실행됨 (PID: $($process.Id))" -ForegroundColor Green

# 2. UI Automation으로 윈도우 찾기
Write-Host "`n[2/4] VortexCut 윈도우 찾는 중..." -ForegroundColor Yellow

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$automation = [System.Windows.Automation.AutomationElement]::RootElement
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty,
    "VortexCut - Professional Video Editor"
)

$window = $automation.FindFirst(
    [System.Windows.Automation.TreeScope]::Children,
    $condition
)

if ($null -eq $window) {
    Write-Host "❌ VortexCut 윈도우를 찾을 수 없습니다" -ForegroundColor Red
    Stop-Process -Id $process.Id -Force
    exit 1
}

Write-Host "✅ 윈도우 발견: $($window.Current.Name)" -ForegroundColor Green

# 3. 재생 버튼 찾기 (▶)
Write-Host "`n[3/4] 재생 버튼 찾는 중..." -ForegroundColor Yellow

$buttonCondition = New-Object System.Windows.Automation.AndCondition(
    @(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button
        )),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            "▶"
        ))
    )
)

$playButton = $window.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    $buttonCondition
)

if ($null -eq $playButton) {
    Write-Host "❌ 재생 버튼(▶)을 찾을 수 없습니다" -ForegroundColor Red
    Write-Host "   모든 버튼 목록:" -ForegroundColor Yellow

    $allButtons = $window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button
        ))
    )

    foreach ($btn in $allButtons) {
        Write-Host "   - $($btn.Current.Name)" -ForegroundColor Gray
    }

    Stop-Process -Id $process.Id -Force
    exit 1
}

Write-Host "✅ 재생 버튼 발견: $($playButton.Current.Name)" -ForegroundColor Green

# 4. 재생 버튼 클릭
Write-Host "`n[4/4] 재생 버튼 클릭..." -ForegroundColor Yellow

try {
    $invokePattern = $playButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invokePattern.Invoke()
    Write-Host "✅ 재생 버튼 클릭 성공!" -ForegroundColor Green

    Write-Host "`n📹 재생 시작됨 - 5초 대기..." -ForegroundColor Cyan
    Start-Sleep -Seconds 5

    # 다시 클릭해서 일시정지
    $invokePattern.Invoke()
    Write-Host "✅ 재생 일시정지" -ForegroundColor Green

    Write-Host "`n✅ 재생 테스트 완료!" -ForegroundColor Green -BackgroundColor Black
    Write-Host "   - 재생 시작: 성공" -ForegroundColor Green
    Write-Host "   - 재생 일시정지: 성공" -ForegroundColor Green
}
catch {
    Write-Host "❌ 재생 버튼 클릭 실패: $_" -ForegroundColor Red
    Stop-Process -Id $process.Id -Force
    exit 1
}

# 5초 후 앱 종료
Write-Host "`n5초 후 앱을 종료합니다..." -ForegroundColor Yellow
Start-Sleep -Seconds 5
Stop-Process -Id $process.Id -Force

Write-Host "`n=== 테스트 종료 ===" -ForegroundColor Green
