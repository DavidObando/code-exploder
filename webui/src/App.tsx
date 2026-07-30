import { BrowserRouter, Routes, Route } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Shell } from './features/shell/Shell';
import { Home } from './features/home/Home';
import { AnalysisProgress } from './features/progress/AnalysisProgress';
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
            {/* In M0 both session routes show progress; /sessions/:id becomes a
                status-based redirect once the tutorial (M2) exists. */}
            <Route path="sessions/:id" element={<AnalysisProgress />} />
            <Route path="sessions/:id/progress" element={<AnalysisProgress />} />
            <Route path="settings" element={<Settings />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
