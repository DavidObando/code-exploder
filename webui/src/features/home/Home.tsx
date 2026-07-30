import { usePageTitle } from '../../lib/usePageTitle';
import { SessionList } from './SessionList';
import { NewSessionForm } from './NewSessionForm';
import styles from './home.module.css';

export function Home() {
  usePageTitle('Code Exploder');

  return (
    <div className={styles.layout}>
      <SessionList />
      <div className={styles.main}>
        <div className={styles.hero}>
          <h1 className={styles.heroTitle}>Explode a codebase</h1>
          <p className={styles.heroSubtitle}>
            Paste a public GitHub repo or PR URL and get a guided tour from a virtual expert.
          </p>
          <NewSessionForm />
        </div>
      </div>
    </div>
  );
}
