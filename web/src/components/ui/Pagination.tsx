import { ChevronLeft, ChevronRight } from 'lucide-react';
import { formatNumber } from '@/lib/format';
import { Button } from './Button';

export function Pagination({
  skip,
  take,
  total,
  onChange,
}: {
  skip: number;
  take: number;
  total: number;
  onChange: (skip: number) => void;
}) {
  const from = total === 0 ? 0 : skip + 1;
  const to = Math.min(skip + take, total);
  return (
    <nav className="flex items-center justify-between gap-3 border-t border-border px-4 py-2.5" aria-label="Pagination">
      <p className="text-sm text-fg-subtle">
        {formatNumber(from)}–{formatNumber(to)} of {formatNumber(total)}
      </p>
      <div className="flex gap-1">
        <Button
          size="sm"
          icon={<ChevronLeft size={14} />}
          disabled={skip === 0}
          onClick={() => onChange(Math.max(0, skip - take))}
        >
          Previous
        </Button>
        <Button size="sm" disabled={skip + take >= total} onClick={() => onChange(skip + take)}>
          Next
          <ChevronRight size={14} />
        </Button>
      </div>
    </nav>
  );
}
