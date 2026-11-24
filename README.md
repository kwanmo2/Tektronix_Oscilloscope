# TekCapture

Tektronix DPO4054 (DPO/MSO4000 계열) USB 화면·파형 캡처 프로그램. OpenChoice Desktop 대체용.

## 왜 OpenChoice Desktop이 장치를 못 찾나
이 PC에는 Keysight IO Libraries 2017과 TekVISA가 함께 설치돼 있다.
- `C:\Windows\SysWOW64\visa32.dll`(32비트 VISA)이 Keysight 것이다. OpenChoice Desktop은 32비트 프로그램이라 이 DLL을 쓴다.
- 스코프(USB VID 0x0699 / PID 0x0401)는 TekVISA 드라이버가 아니라 IVI/Keysight USBTMC 드라이버에 잡혀 있다.

64비트 VISA(`visa64.dll`)로는 정상 통신된다. TekCapture는 이 경로를 쓰므로 OpenChoice를 고칠 필요가 없다.

## 빌드 / 실행
```
cd src\TekCapture
dotnet build -c Release
bin\Release\net10.0-windows\TekCapture.exe
```
.NET 10 Desktop Runtime이 필요하다. 다른 PC에 배포하려면:
```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## 기능
- 화면 캡처: PNG, Ink Saver(흰 배경) 옵션 (`HARDCopy STARt`)
- 파형 캡처: CH1–4, MATH. 16비트로 전체 레코드를 받아 전압·시간 단위로 변환하고 CSV로 저장
- 파형 뷰어 (스코프와 같은 10×8 div 화면)
  - 시간축: Time/div(1-2-5 단계 버튼, 직접 입력), 중심 시간, 스크롤바
  - 마우스: 휠로 확대/축소(커서 위치 기준), 드래그로 이동, 우클릭 드래그로 구간 확대, 더블클릭으로 전체 보기
  - 채널별 V/div와 위치(div), 표시 켜기/끄기, 자동, 스코프 설정, 분리 배치
  - 왼쪽 채널 마커를 드래그해 위치 조절. 선택 채널은 Shift+휠로 V/div, Ctrl+휠이나 ↑↓로 위치 조절
  - 마우스 위치의 시간과 각 채널 값 표시. 많이 확대하면 샘플 점이 보임
- 채널 라벨: 처음에는 스코프의 `CHx:LABel` 값을 쓰고, 앱에서 고친 값은 채널별로 기억한다. 그래프, 값 표시, CSV 헤더에 모두 나온다.
- 레코드 길이(1k~10M) 설정. 바꿀 때 현재 time/div를 유지하려고 시도한다.
- Run / Stop / Single
- 저장 위치 기본값: `문서\TekCapture\<모델>_<날짜_시간>_screen.png / _wave.csv`

## 헤드리스 셀프테스트
```
TekCapture.exe --selftest <출력폴더>
```
`selftest.log`, `screen.png`, `wave.csv`를 만든다.
