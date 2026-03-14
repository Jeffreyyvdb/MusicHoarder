import tailwindcss from '@tailwindcss/vite';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig, loadEnv } from 'vite';

export default defineConfig(({ mode }) => {
	const env = loadEnv(mode, process.cwd(), '');

	// Aspire injects service URLs via services__{resourceName}__{endpoint}__{index}
	// Resource name is "musichoarder-api" (dash); use bracket access for env vars with dashes
	const apiUrl =
		env['services__musichoarder-api__https__0'] ??
		env['services__musichoarder-api__http__0'];

	return {
		plugins: [tailwindcss(), sveltekit()],
		server: {
			port: env.PORT ? parseInt(env.PORT, 10) : 5173,
			proxy: apiUrl
				? {
						'/api': {
							target: apiUrl,
							changeOrigin: true,
							secure: false,
							rewrite: (path) => path.replace(/^\/api/, '')
						}
					}
				: undefined
		}
	};
});
