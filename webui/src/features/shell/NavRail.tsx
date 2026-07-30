import { NavLink } from 'react-router-dom';
import styles from './shell.module.css';

const items = [
  { to: '/', glyph: '⌂', label: 'Home', end: true },
  { to: '/settings', glyph: '⚙', label: 'Settings', end: false },
];

/** Left icon rail. The Home badge counts sessions currently queued or analyzing. */
export function NavRail({ activeCount = 0 }: { activeCount?: number }) {
  return (
    <nav className={styles.rail} aria-label="Primary">
      <div className={styles.railBrand}>
        CODE
        <br />
        EXPLODER
      </div>
      {items.map((item) => (
        <NavLink
          key={item.to}
          to={item.to}
          end={item.end}
          className={({ isActive }) => (isActive ? styles.railItemActive : styles.railItem)}
        >
          <span className={styles.railGlyph} aria-hidden="true">
            {item.glyph}
          </span>
          {item.label}
          {item.label === 'Home' && activeCount > 0 && (
            <span className={styles.railCount} aria-label={`${activeCount} session${activeCount === 1 ? '' : 's'} analyzing`}>
              {activeCount}
            </span>
          )}
        </NavLink>
      ))}
    </nav>
  );
}
