export interface FestivalGreeting {
  // YYYY-MM-DD for a specific year (moving festivals), or MM-DD for a fixed annual date.
  start: string;
  end: string;
  label: string;
  title: string;
  emphasis: string;
  message: string;
  motif: string;
}

// Edit this list before each year for moving-date festivals. Dates are inclusive in India (IST).
// Place specific-year events above annual events if their dates overlap.
export const festivalSchedule: FestivalGreeting[] = [
  {
    start: '2026-10-11', end: '2026-10-19', label: 'NINE NIGHTS OF CELEBRATION',
    title: 'Wishing you a', emphasis: 'joyful Navratri.',
    message: 'May these nine nights bring warmth, joy and togetherness to you and your loved ones.',
    motif: '✦'
  },
  {
    start: '2026-11-08', end: '2026-11-10', label: 'A FESTIVAL OF LIGHT',
    title: 'Wishing you a', emphasis: 'joyful Diwali.',
    message: 'May your days be bright and your home filled with warmth. Explore little finds for the celebrations.',
    motif: '✦'
  },
  {
    start: '01-01', end: '01-01', label: 'A FRESH START',
    title: 'Wishing you a', emphasis: 'happy New Year.',
    message: 'Here’s to new beginnings, thoughtful moments and all the little joys ahead.',
    motif: '✳'
  },
  {
    start: '01-26', end: '01-26', label: 'CELEBRATING TOGETHER',
    title: 'Happy', emphasis: 'Republic Day.',
    message: 'Warm wishes to you and your family on this special day across India.',
    motif: '✦'
  },
  {
    start: '08-15', end: '08-15', label: 'CELEBRATING TOGETHER',
    title: 'Happy', emphasis: 'Independence Day.',
    message: 'Wishing you a day of pride, joy and togetherness.',
    motif: '✦'
  },
  {
    start: '12-25', end: '12-25', label: 'SEASON’S GREETINGS',
    title: 'Wishing you a', emphasis: 'merry Christmas.',
    message: 'May this season bring warmth, laughter and thoughtful moments to share.',
    motif: '✳'
  }
];

export function festivalForIndiaDate(now: Date, schedule: FestivalGreeting[] = festivalSchedule): FestivalGreeting | null {
  // Shift UTC to IST before taking the date, independent of the visitor's timezone.
  const indiaDate = new Date(now.getTime() + 330 * 60 * 1000).toISOString().slice(0, 10);
  const monthDay = indiaDate.slice(5);
  return schedule.find(entry => entry.start.length === 10
    ? indiaDate >= entry.start && indiaDate <= entry.end
    : monthDay >= entry.start && monthDay <= entry.end) || null;
}
