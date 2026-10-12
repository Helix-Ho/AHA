# OpenAI 개발 문서

Codex의 저장소 지침과 개발 문서 관리에 관한 공식 자료다. 검토일은 2026-10-08이다. 원문을 복제하지 않으며 별도 SDK나 실행 코드는 사용하지 않는다.

- [AGENTS.md 안내](https://learn.chatgpt.com/docs/agent-configuration/agents-md): Codex가 지속적인 저장소 지침을 읽는 진입점과 지침 범위를 확인했다.
- [Best practices](https://learn.chatgpt.com/guides/best-practices#make-guidance-reusable-with-agentsmd): 짧고 정확한 AGENTS에서 필요한 문서로 연결하고, 변경에 맞는 검사·검토 기준을 명시하는 방식을 참고한다.
- [Iterating development workflows with Codex](https://developers.openai.com/cookbook/examples/codex/iterating-development-workflows-with-codex): 특히 `Context files`의 중요도 판단, 판단 근거 연결, 중복·임시 진행 기록 제외, 인계 전 검토 방식을 참고했다.
- [Using PLANS.md for multi-hour problem solving](https://developers.openai.com/cookbook/articles/codex_exec_plans): 복잡한 변경의 실행 계획을 관리하는 사례다. 현재 구현의 계약·설계 설명과 실행 계획의 역할을 구분할 때 참고한다.

AHA의 [문서 정책](../../README.md#문서-관리)은 짧은 진입점에서 소유자의 계약과 필요한 설계·검사로 연결한다. [History 기준](../../README.md#history-기록-기준)은 다음 판단에 영향을 주는 기록을 선별하고 중복·보존 가치 없는 내용을 정리한다. 현재 계약과 과거 이유를 구분하고 원본에 링크한다.

Cookbook의 문서 구성은 프로젝트에 맞춰 선택하는 예시다. AHA의 `contract.md`·`architecture.md`·`HISTORY.md`와 소유자별 폴더는 AHA가 정한 형식이다. 예제의 진행 장부나 승인 절차를 함께 도입하지 않으며, 원문의 후속 개정도 AHA 정책을 자동으로 바꾸지 않는다.
