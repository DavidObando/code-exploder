import { useEffect } from 'react';

/** Sets document.title for the current page. */
export function usePageTitle(title: string) {
  useEffect(() => {
    document.title = title;
  }, [title]);
}
