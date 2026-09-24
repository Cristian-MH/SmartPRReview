import { expect, test, type Route } from '@playwright/test'

const catalog = {
  defaultProvider: 'OpenAI',
  providers: ['OpenAI', 'Gemini', 'DeepSeek'].map(provider => ({
    provider, hasServerCredentials: true, classificationModel: `${provider}-classify`, reviewModel: `${provider}-review`,
    models: [{ id: `${provider}-classify`, classification: true, review: false, tools: false }, { id: `${provider}-review`, classification: false, review: true, tools: true }],
  })),
}
test.beforeEach(async ({ page }) => {
  await page.route('**/api/ai/models', route => route.fulfill({ json: catalog }))
})

test('sends an AI key per review, clears it when switching providers and never persists it', async ({ page }) => {
  await page.route('**/api/ai/models', route => route.fulfill({ json: {
    ...catalog, providers: catalog.providers.map(p => ({ ...p, hasServerCredentials: false })),
  } }))
  await page.route('**/api/pull-requests/list', route => route.fulfill({ json: [pull] }))
  await page.route('**/api/reviews/stream', async route => {
    expect(route.request().postDataJSON()).toMatchObject({
      aiApiKey: 'gemini-session-key', ai: { provider: 'Gemini', classificationModel: 'Gemini-classify', reviewModel: 'Gemini-review' },
    })
    await streamReply(route, { id: 'review-id', status: 'Completed', summary: 'Listo', findings: [], pullRequest: null })
  })
  await page.goto('/')
  await page.getByRole('button', { name: 'Cargar pull requests' }).click()
  await page.getByRole('button', { name: /Mejorar navegación/ }).click()
  await expect(page.getByRole('button', { name: 'Consultar revisión' })).toBeDisabled()
  const key = page.getByLabel('Clave de API de IA')
  await expect(key).toHaveAttribute('type', 'password')
  await key.fill('openai-session-key')
  await expect(page.getByRole('button', { name: 'Consultar revisión' })).toBeEnabled()
  await page.getByLabel('Proveedor de IA').selectOption('Gemini')
  await expect(key).toHaveValue('')
  await expect(page.getByRole('button', { name: 'Consultar revisión' })).toBeDisabled()
  await key.fill('gemini-session-key')
  await page.getByRole('button', { name: 'Consultar revisión' }).click()
  await expect(page.getByText('Consulta completada', { exact: true })).toBeVisible()
  expect(await page.evaluate(() => JSON.stringify([localStorage, sessionStorage]))).not.toContain('session-key')
  await page.reload()
  await expect(page.getByLabel('Clave de API de IA')).toHaveValue('')
})
const streamReply = (route: Route, review: unknown) => route.fulfill({
  contentType: 'application/x-ndjson', body: [
    { type: 'started', reviewId: 'review-id', stage: 'starting', message: 'Iniciando revisión.' },
    { type: 'progress', reviewId: 'review-id', stage: 'classification', message: 'Clasificando cambios.' },
    { type: 'result', reviewId: 'review-id', stage: 'finished', message: 'Resultado final.', review },
  ].map(event => JSON.stringify(event)).join('\n') + '\n',
})

const pull = { number: 32, title: 'Mejorar navegación', description: '<script>alert(1)</script>', state: 'open', isDraft: false, author: 'ucr-labs', webUrl: 'https://github.com/UCR-Labs/Coope-Web/pull/32', baseReference: 'main', headReference: 'feature/nav', createdAt: '2026-09-01T00:00:00Z', updatedAt: '2026-09-22T00:00:00Z', closedAt: null, mergedAt: null }

test('status cards filter open, merged and all PRs together with search', async ({ page }) => {
  await page.route('**/api/pull-requests/list', route => route.fulfill({ json: [
    pull,
    { ...pull, number: 33, title: 'Closed PR', state: 'closed' },
    { ...pull, number: 34, title: 'Merged PR', state: 'closed', mergedAt: '2026-09-22T00:00:00Z' },
    { ...pull, number: 35, title: 'Draft PR', isDraft: true },
  ] }))
  await page.goto('/')
  await page.getByRole('button', { name: 'Cargar pull requests' }).click()
  await expect(page.locator('.pull-row')).toHaveCount(4)
  await page.getByRole('button', { name: /Closed PR/ }).click()
  await page.getByRole('button', { name: /^Abiertos/ }).click()
  await expect(page.getByRole('button', { name: /^Abiertos/ })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.locator('.pull-row')).toHaveCount(2)
  await expect(page.locator('.pull-list')).not.toContainText('Closed PR')
  await expect(page.locator('.pull-list')).not.toContainText('Merged PR')
  await expect(page.locator('.detail-title')).toHaveCount(0)
  await page.getByLabel('Buscar pull requests').fill('35')
  await expect(page.locator('.pull-row')).toHaveCount(1)
  await expect(page.locator('.pull-row')).toContainText('Draft PR')
  await page.getByLabel('Buscar pull requests').fill('')
  await page.getByRole('button', { name: /^Integrados/ }).click()
  await expect(page.locator('.pull-row')).toHaveCount(1)
  await expect(page.locator('.pull-row')).toContainText('Merged PR')
  await page.getByRole('button', { name: /^Pull requests 4/ }).click()
  await expect(page.locator('.pull-row')).toHaveCount(4)
})

test('connects, filters, reviews files and keeps credentials out of storage', async ({ page }) => {
  await page.route('**/api/pull-requests/list', async route => {
    expect(route.request().postDataJSON()).toEqual({ location: 'https://github.com/UCR-Labs/Coope-Web', state: 'all', gitHubToken: 'test-token' })
    await route.fulfill({ json: [pull, { ...pull, number: 33, title: 'Actualizar estilos' }] })
  })
  await page.route('**/api/reviews/stream', async route => {
    expect(route.request().postDataJSON()).toMatchObject({ provider: 'GitHub', pullRequestNumber: 32, gitHubToken: 'test-token', location: 'https://github.com/UCR-Labs/Coope-Web' })
    await streamReply(route, { id: 'review-id', status: 'Completed', summary: 'Retrieved GitHub pull request #32.', error: null, findings: [], pullRequest: { ...pull, changedFileCount: 1, additions: 2, deletions: 1, commitCount: 1, files: [{ path: 'src/nav.vue', additions: 2, deletions: 1, patch: '@@ -1 +1 @@\n-old\n+new' }] } })
  })
  await page.goto('/')
  await page.getByLabel('Token de acceso').fill('test-token')
  await page.getByRole('button', { name: 'Cargar pull requests' }).click()
  await page.getByLabel('Buscar pull requests').fill('32')
  await expect(page.getByRole('button', { name: /Actualizar estilos/ })).toHaveCount(0)
  await page.getByRole('button', { name: /Mejorar navegación/ }).click()
  await expect(page.locator('.description')).toHaveText('<script>alert(1)</script>')
  // Editing the connection form must not change the repository for the loaded PR.
  await page.getByLabel('Repositorio de GitHub').fill('https://github.com/another/repo')
  await page.getByRole('button', { name: 'Consultar revisión' }).click()
  await expect(page.getByText('Consulta completada', { exact: true })).toBeVisible()
  await page.locator('summary').click()
  await expect(page.locator('pre')).toContainText('+new')
  expect(await page.evaluate(() => `${JSON.stringify(localStorage)} ${JSON.stringify(sessionStorage)}`)).not.toContain('test-token')
})

test('handles validation, failed reviews and backend errors', async ({ page }) => {
  await page.route('**/api/pull-requests/list', route => route.fulfill({ json: [pull] }))
  await page.route('**/api/reviews/stream', route => streamReply(route, { status: 'Failed', error: 'GitHub denied access.', findings: [], pullRequest: null }))
  await page.goto('/')
  await page.getByLabel('Repositorio de GitHub').fill('https://example.com/repo/test')
  await page.getByRole('button', { name: 'Cargar pull requests' }).click()
  await expect(page.getByRole('alert')).toContainText('Ingresa la URL')
  await page.getByLabel('Repositorio de GitHub').fill('https://github.com/UCR-Labs/Coope-Web')
  await page.getByRole('button', { name: 'Cargar pull requests' }).click()
  await page.getByRole('button', { name: /Mejorar navegación/ }).click()
  await page.getByRole('button', { name: 'Consultar revisión' }).click()
  await expect(page.getByRole('alert')).toHaveText('GitHub denied access.')
  await page.route('**/api/pull-requests/list', route => route.fulfill({ status: 502, json: { detail: 'GitHub unavailable.' } }))
  await page.getByRole('button', { name: 'Cargar pull requests' }).click()
  await expect(page.getByRole('alert')).toHaveText('GitHub unavailable.')
  await expect(page.getByText('Consulta completada', { exact: true })).toHaveCount(0)
})

test('empty results and mobile layout', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.route('**/api/pull-requests/list', route => route.fulfill({ json: [] }))
  await page.goto('/')
  await page.getByRole('button', { name: 'Cargar pull requests' }).click()
  await expect(page.getByText('Sin resultados', { exact: true })).toBeVisible()
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
})

test('provider selection resets models and renders partial AI evidence', async ({ page }) => {
  await page.route('**/api/pull-requests/list', route => route.fulfill({ json: [pull] }))
  await page.route('**/api/reviews/stream', async route => {
    expect(route.request().postDataJSON().ai).toEqual({ provider: 'Gemini', classificationModel: 'Gemini-classify', reviewModel: 'Gemini-review' })
    await streamReply(route, { id: 'review-id', status: 'Failed', error: 'Un especialista no completó la revisión.', findings: [], pullRequest: null,
      ai: { selection: route.request().postDataJSON().ai, classification: { category: 'fix', rationale: 'Corrige navegación', scope: 'Menú', evidence: ['src/nav.vue'], confidence: 0.9 }, technologies: ['vue-typescript'], skills: [{ id: 'vue-typescript', version: '1.0.0', hash: 'abc' }], specialists: [], execution: { status: 'Skipped', reason: 'Runner no configurado.', steps: [] }, limitations: ['Presupuesto agotado.'], recommendation: 'NeedsHumanReview', headSha: 'abc', baseSha: 'def', usage: [{ stage: 'classification', provider: 'Gemini', model: 'Gemini-classify', inputTokens: 100, cachedInputTokens: null, outputTokens: 20, estimatedCost: null, durationMs: 10 }] } })
  })
  await page.goto('/')
  await expect(page.getByLabel('Proveedor de IA')).toHaveValue('OpenAI')
  await page.getByLabel('Proveedor de IA').selectOption('DeepSeek')
  await expect(page.getByLabel('Modelo de clasificación')).toHaveValue('DeepSeek-classify')
  await page.getByLabel('Proveedor de IA').selectOption('Gemini')
  await expect(page.getByLabel('Modelo de revisión')).toHaveValue('Gemini-review')
  await page.getByRole('button', { name: 'Cargar pull requests' }).click()
  await page.getByRole('button', { name: /Mejorar navegación/ }).click()
  await page.getByRole('button', { name: 'Consultar revisión' }).click()
  await expect(page.getByText('Clasificando cambios.')).toBeVisible()
  await expect(page.getByText('Requiere revisión humana', { exact: true })).toBeVisible()
  await expect(page.getByText('Corrige navegación', { exact: true })).toBeVisible()
  await expect(page.getByText('Presupuesto agotado.', { exact: true })).toBeVisible()
  await expect(page.locator('.usage-table')).toContainText('N/D')
})

test('unconfigured AI disables review without hiding repository browsing', async ({ page }) => {
  await page.route('**/api/ai/models', route => route.fulfill({ json: { defaultProvider: 'OpenAI', providers: [] } }))
  await page.route('**/api/pull-requests/list', route => route.fulfill({ json: [pull] }))
  await page.goto('/')
  await page.getByRole('button', { name: 'Cargar pull requests' }).click()
  await page.getByRole('button', { name: /Mejorar navegación/ }).click()
  await expect(page.getByRole('button', { name: 'Consultar revisión' })).toBeDisabled()
  await expect(page.getByText(/No hay modelos disponibles/)).toBeVisible()
})

test('truncated stream is never shown as a completed review', async ({ page }) => {
  await page.route('**/api/pull-requests/list', route => route.fulfill({ json: [pull] }))
  await page.route('**/api/reviews/stream', route => route.fulfill({ contentType: 'application/x-ndjson', body: JSON.stringify({ type: 'started', reviewId: 'id', stage: 'starting', message: 'Iniciando revisión.' }) + '\n' }))
  await page.goto('/')
  await page.getByRole('button', { name: 'Cargar pull requests' }).click()
  await page.getByRole('button', { name: /Mejorar navegación/ }).click()
  await page.getByRole('button', { name: 'Consultar revisión' }).click()
  await expect(page.getByRole('alert')).toContainText('Se interrumpió la conexión')
  await expect(page.getByText('Consulta completada', { exact: true })).toHaveCount(0)
})
