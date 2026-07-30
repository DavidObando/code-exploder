import { BrowserRouter, Routes, Route } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Shell } from './features/shell/Shell';
import { Home } from './features/home/Home';
import { AnalysisProgress } from './features/progress/AnalysisProgress';
import { SessionRedirect } from './features/tutorial/SessionRedirect';
import { TutorialLayout } from './features/tutorial/TutorialLayout';
import { SectionView, TutorialIndexRedirect } from './features/tutorial/SectionView';
import { Settings } from './features/settings/Settings';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: { refetchOnWindowFocus: false },
  },
});

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <Routes>
          <Route element={<Shell />}>
            <Route index element={<Home />} />
            <Route path="sessions/:id" element={<SessionRedirect />} />
            <Route path="sessions/:id/progress" element={<AnalysisProgress />} />
            <Route path="sessions/:id/learn" element={<TutorialLayout />}>
              <Route index element={<TutorialIndexRedirect />} />
              <Route path=":sectionSlug" element={<SectionView />} />
            </Route>
            <Route path="settings" element={<Settings />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
