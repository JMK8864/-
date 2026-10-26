# CJ.SCADA.Solution 분석 및 테스트 환경 자료

| 문서 | 내용 |
|---|---|
| [docs/01_CJ.SCADA-최종분석보고서.md](docs/01_CJ.SCADA-최종분석보고서.md) | 구조, 프로젝트별 기능, 중요 클래스, DB 구조, 문제점(보안·안정성·빌드), 테스트 결과, 권장 조치 |
| [docs/02_SCADA-테스트환경-구축가이드.md](docs/02_SCADA-테스트환경-구축가이드.md) | Windows + VS2026 + WSL Docker(TimescaleDB, nginx) 테스트 환경을 **한 단계씩** 구축 (0~14단계 + 부록) |
| [docs/03_부록-REST-API.md](docs/03_부록-REST-API.md) | REST 엔드포인트 목록, 화면별 호출 위치, 호출 예 |
| [docs/04_주요클래스-함수-설명서.md](docs/04_주요클래스-함수-설명서.md) | app-server.ftms / DxfBlazorViewer.Client 주요 클래스·함수 설명, 특징, 주의할 점 |
| [docs/04_주요클래스-함수-설명서.pdf](docs/04_주요클래스-함수-설명서.pdf) | 위 문서의 PDF 판 (34쪽, 시퀀스·클래스·상태 도식 15종 포함) |
| [docs/04_주요클래스-함수-발표.pptx](docs/04_주요클래스-함수-발표.pptx) | 발표용 PPT (23장, 도식 + 핵심 요약, 슬라이드 노트 포함) |
| [docs/05_데이터포맷-프로토콜-명세서.md](docs/05_데이터포맷-프로토콜-명세서.md) | 에뮬레이터 ↔ app-server(TCP), app-server ↔ Viewer(REST·SignalR) 데이터 포맷·프로토콜, 문제점 P1~P12 |
| [docs/05_데이터포맷-프로토콜-명세서.pdf](docs/05_데이터포맷-프로토콜-명세서.pdf) | 위 문서의 PDF 판 (20쪽, 도식 5종) |
| [docs/diagrams/](docs/diagrams/) | 도식 원본 (Mermaid `src/*.mmd`) 과 PNG (`png/`) — d01~d15: 04 설명서, p01~p05: 05 명세서 |
| [setup/](setup/README.md) | 가이드 단계별 SQL, 설정 파일, nginx 설정 |

구성 요약
```
브라우저 → http://localhost/ → [WSL Docker] ftms-nginx :80
                                   ├ /api /chathub /health → app-server.ftms :8121 (Windows VS)
                                   └ 그 외                → DxfBlazorViewer :8122 (Windows VS)
app-server.ftms → 127.0.0.1:5432 → [WSL Docker] ftms-db (PostgreSQL 17 + TimescaleDB)
도크 에뮬레이터 → TCP 6004 → app-server.ftms
```
