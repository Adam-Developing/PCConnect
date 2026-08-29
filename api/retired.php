<?php

declare(strict_types=1);

http_response_code(410);
header('Content-Type: application/problem+json');
header('Cache-Control: no-store');
header('X-Content-Type-Options: nosniff');
header('Content-Security-Policy: default-src \'none\'; frame-ancestors \'none\'');

echo json_encode([
    'type' => 'https://pcconnect.adamdeveloping.co.uk/problems/legacy-php-retired',
    'title' => 'Legacy PHP service retired',
    'status' => 410,
    'code' => 'legacy_php_retired',
    'detail' => 'This source tree is not a deployable service. Route approved compatibility traffic to the PCConnect v2 API.',
], JSON_THROW_ON_ERROR);
