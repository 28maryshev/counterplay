# -*- coding: utf-8 -*-
"""
KeyReminder — пока нового ключа Riot нет, демон напоминает о нём.

Повод (журнал 2026-11-04): ключ истёк 3 ноября, демон один раз попросил новый
и замолчал — сбор стоял, пока владелец сам не заметил. Теперь просьба
повторяется: через час после первой, потом через два, дальше каждые три часа,
пока ключ не придёт.

Часы и сон подменены, Discord тоже — проверка идёт мгновенно, сеть не нужна,
рабочих баз и папок не трогает (всё во временной папке).

Что меряет:
  1. расписание: напоминания уходят через 1, 3, 6, 9, 12 ч после просьбы, не
     раньше и не позже одного опроса файла ключа;
  2. просьба одна: «🔑 Нужен свежий ключ» уходит один раз, остальное —
     напоминания «⏰»;
  3. «сбор стоит N» считается от истечения ключа, а не от просьбы (между ними
     идёт публикация);
  4. ключ пришёл — напоминания прекращаются, ключ возвращён;
  5. новое ожидание начинает расписание заново (снова через час).

    python tests/KeyReminder/check.py        # 0 — всё хорошо, 1 — нет
"""
import os
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TMP = Path(tempfile.mkdtemp(prefix='cp-keyreminder-'))
os.environ.update(DB_PATH=str(TMP / 'none.db'), CONTROL_DIR=str(TMP / 'control'),
                  GITHUB_TOKEN='', DISCORD_WEBHOOK='')
sys.path.insert(0, str(ROOT / 'pipeline'))
import collector_service as svc      # noqa: E402

(TMP / 'control').mkdir()
sys.stdout.reconfigure(encoding='utf-8')   # консоль Windows иначе падает на ✓
H = 3600
failures: list[str] = []


def fail(msg: str):
    failures.append(msg)
    print('  ✗ ' + msg)


class Clock:
    """Подменные часы: sleep двигает время, а не ждёт."""
    def __init__(self, start: float):
        self.t = start

    def time(self) -> float:
        return self.t

    def sleep(self, sec: float):
        self.t += sec


def run_wait(clock: Clock, key_after: float, since: float | None):
    """Гоняет wait_for_key, пока в файле не появится ключ (через key_after
    секунд от старта). Возвращает (ключ, [(секунды от старта, текст)])."""
    sent: list[tuple[float, str]] = []
    start = clock.t
    svc.KEY_FILE.unlink(missing_ok=True)

    def sleep(sec):
        clock.sleep(sec)
        if clock.t - start >= key_after and not svc.KEY_FILE.exists():
            svc.KEY_FILE.write_text('RGAPI-test', encoding='utf-8')

    svc.time.time = clock.time
    svc.time.sleep = sleep
    svc.notify = lambda text: sent.append((clock.t - start, text))
    key = svc.wait_for_key(since)
    return key, sent


def main() -> int:
    real_time, real_sleep = svc.time.time, svc.time.sleep
    try:
        print('1–4. ожидание 13 часов после истечения ключа')
        clock = Clock(1_000_000.0)
        expired = clock.t - 40 * 60          # публикация шла 40 минут
        key, sent = run_wait(clock, key_after=13 * H, since=expired)

        asks = [t for t, s in sent if s.startswith('🔑')]
        reminders = [(t, s) for t, s in sent if s.startswith('⏰')]
        if len(asks) != 1 or asks[0] != 0:
            fail(f'2: просьба о ключе должна уйти один раз сразу, а ушла {len(asks)} раз')
        want = [1 * H, 3 * H, 6 * H, 9 * H, 12 * H]
        got = [t for t, _ in reminders]
        if len(got) != len(want) or any(not (w <= g < w + svc.POLL + 1)
                                        for w, g in zip(want, got)):
            fail('1: напоминания должны уйти через '
                 + ', '.join(f'{w // H} ч' for w in want)
                 + ', а ушли через ' + ', '.join(f'{g / H:.2f} ч' for g in got))
        else:
            print('  ✓ напоминания через ' + ', '.join(f'{w // H} ч' for w in want))
        if reminders and '1 ч 40 мин' not in reminders[0][1]:
            fail(f'3: первое напоминание должно сказать «сбор стоит 1 ч 40 мин» '
                 f'(от истечения ключа), а сказало: {reminders[0][1]!r}')
        elif reminders:
            print('  ✓ «сбор стоит» считается от истечения ключа')
        if key != 'RGAPI-test':
            fail(f'4: ключ должен вернуться, вернулось {key!r}')
        elif not any(t > 12 * H for t, _ in reminders):
            print('  ✓ ключ пришёл — напоминания прекратились')

        print('5. второе ожидание начинает расписание заново')
        key, sent = run_wait(clock, key_after=int(1.5 * H), since=None)
        reminders = [t for t, s in sent if s.startswith('⏰')]
        if len(reminders) != 1 or not (H <= reminders[0] < H + svc.POLL + 1):
            fail(f'5: в новом ожидании напоминание одно и через час, а их '
                 f'{len(reminders)}: {[round(t / H, 2) for t in reminders]} ч')
        else:
            print('  ✓ снова через час')
    finally:
        svc.time.time, svc.time.sleep = real_time, real_sleep

    if failures:
        print(f'\nПРОВАЛ: {len(failures)}')
        return 1
    print('\nВсё хорошо.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
