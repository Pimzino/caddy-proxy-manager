import { Link } from 'react-router';
import { Compass } from 'lucide-react';
import { EmptyState } from '@/components/ui';

export default function NotFoundPage() {
  return (
    <EmptyState
      icon={<Compass size={18} />}
      title="Page not found"
      description="The address does not match any page in the console."
      action={
        <Link to="/" className="text-sm font-medium text-accent-text hover:underline">
          Go to the dashboard
        </Link>
      }
    />
  );
}
