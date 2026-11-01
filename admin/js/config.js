// Public client config. The anon key is meant to be public: every table is
// protected by RLS and admin RPCs check parent_control.app_admins.
export const SUPABASE_URL = 'https://opufwbpkuofxhfmlqhqq.supabase.co';
export const SUPABASE_KEY =
  'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im9wdWZ3YnBrdW9meGhmbWxxaHFxIiwicm9sZSI6ImFub24iLCJpYXQiOjE3OTM0ODE2NDcsImV4cCI6MjEwOTA1NzY0N30.LPBLawYAVFyGB-aawM8kJshaeyvcAdjHsFE1SKa3mbU';
export const DB_SCHEMA = 'parent_control';
export const SUPABASE_JS_URL = 'https://cdn.jsdelivr.net/npm/@supabase/supabase-js@2.49.4/+esm';
