<?php

declare(strict_types=1);

function requiredEnvironment(string $name): string
{
    $value = getenv($name);
    if ($value === false || trim($value) === '') {
        throw new RuntimeException("Required deployment environment variable is missing.");
    }
    return $value;
}

$dbConfig = [
    'host' => requiredEnvironment('PCCONNECT_LEGACY_DB_HOST'),
    'user' => requiredEnvironment('PCCONNECT_LEGACY_DB_USER'),
    'pass' => requiredEnvironment('PCCONNECT_LEGACY_DB_PASSWORD'),
    'db' => requiredEnvironment('PCCONNECT_LEGACY_DB_NAME'),
];
