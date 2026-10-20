# CJ.SCADA.Solution 분석 및 테스트 환경 자료

| 문서 | 내용 |
|---|---|
| [docs/01_CJ.SCADA-최종분석보고서.md](docs/01_CJ.SCADA-최종분석보고서.md) | 구조, 프로젝트별 기능, 중요 클래스, DB 구조, 문제점(보안·안정성·빌드), 테스트 결과, 권장 조치 |
| [docs/02_SCADA-테스트환경-구축가이드.md](docs/02_SCADA-테스트환경-구축가이드.md) | Windows + VS2026 + WSL Docker(TimescaleDB, nginx) 테스트 환경을 **한 단계씩** 구축 (0~14단계 + 부록) |
| [docs/03_부록-REST-API.md](docs/03_부록-REST-API.md) | REST 엔드포인트 목록, 화면별 호출 위치, 호출 예 |
| [docs/04_주요클래스-함수-설명서.md](docs/04_주요클래스-함수-설명서.md) | app-server.ftms / DxfBlazorViewer.Client 주요 클래스·함수 설명, 특징, 주의할 점 |
| [setup/](setup/README.md) | 가이드 단계별 SQL, 설정 파일, nginx 설정 |

구성 요약
```
브라우저 → http://localhost/ → [WSL Docker] ftms-nginx :80
                                   ├ /api /chathub /health → app-server.ftms :8121 (Windows VS)
                                   └ 그 외                → DxfBlazorViewer :8122 (Windows VS)
app-server.ftms → 127.0.0.1:5432 → [WSL Docker] ftms-db (PostgreSQL 17 + TimescaleDB)
도크 에뮬레이터 → TCP 6004 → app-server.ftms
```
