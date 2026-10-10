import { festivalForIndiaDate } from './festival-schedule';

describe('festivalForIndiaDate', () => {
  it('shows Navratri from October 11 through October 19, 2026 in India time', () => {
    expect(festivalForIndiaDate(new Date('2026-10-10T18:29:00Z'))).toBeNull();
    expect(festivalForIndiaDate(new Date('2026-10-10T18:30:00Z'))!.emphasis).toBe('joyful Navratri.');
    expect(festivalForIndiaDate(new Date('2026-10-19T18:29:00Z'))!.emphasis).toBe('joyful Navratri.');
    expect(festivalForIndiaDate(new Date('2026-10-19T18:30:00Z'))).toBeNull();
  });

  it('uses India time instead of the visitor or UTC date', () => {
    const greeting = festivalForIndiaDate(new Date('2026-11-07T19:00:00Z'));
    expect(greeting && greeting.emphasis).toBe('joyful Diwali.');
  });

  it('stops a dated greeting after the last India calendar day', () => {
    expect(festivalForIndiaDate(new Date('2026-11-10T18:29:00Z'))!.emphasis).toBe('joyful Diwali.');
    expect(festivalForIndiaDate(new Date('2026-11-10T18:30:00Z'))).toBeNull();
  });

  it('repeats fixed-date greetings every year and otherwise falls back', () => {
    expect(festivalForIndiaDate(new Date('2028-12-24T19:00:00Z'))!.emphasis).toBe('merry Christmas.');
    expect(festivalForIndiaDate(new Date('2027-11-08T12:00:00Z'))).toBeNull();
  });
});
